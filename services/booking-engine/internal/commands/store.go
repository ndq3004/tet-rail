package commands

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"time"
)

var ErrIdempotencyConflict = errors.New("idempotency key was used with a different request")

type Result struct {
	RequestID, Status, HoldID string
	ExpiresAt                 *time.Time
}
type Store struct{ pool *pgxpool.Pool }

func NewStore(pool *pgxpool.Pool) *Store { return &Store{pool} }
func (s *Store) Begin(ctx context.Context, command HoldRequestedV1) (Result, bool, error) {
	body, err := json.Marshal(command.Segments)
	if err != nil {
		return Result{}, false, err
	}
	result := Result{RequestID: command.RequestID, Status: "QUEUED"}
	_, err = s.pool.Exec(ctx, `INSERT INTO booking.hold_requests(id,owner_id,idempotency_key,payload,status) VALUES($1,$2,$3,$4,'QUEUED')`, command.RequestID, command.OwnerID, command.IdempotencyKey, body)
	if err == nil {
		return result, true, nil
	}
	var payload []byte
	err = s.pool.QueryRow(ctx, `SELECT id::text,payload,status,COALESCE(hold_id::text,''),expires_at FROM booking.hold_requests WHERE owner_id=$1 AND idempotency_key=$2`, command.OwnerID, command.IdempotencyKey).Scan(&result.RequestID, &payload, &result.Status, &result.HoldID, &result.ExpiresAt)
	if err != nil {
		return Result{}, false, err
	}
	if string(payload) != string(body) {
		return Result{}, false, ErrIdempotencyConflict
	}
	return result, false, nil
}
func (s *Store) Delete(ctx context.Context, id string) error {
	_, err := s.pool.Exec(ctx, `DELETE FROM booking.hold_requests WHERE id=$1 AND status='QUEUED'`, id)
	return err
}
func (s *Store) Get(ctx context.Context, id string) (Result, error) {
	var r Result
	err := s.pool.QueryRow(ctx, `SELECT id::text,status,COALESCE(hold_id::text,''),expires_at FROM booking.hold_requests WHERE id=$1`, id).Scan(&r.RequestID, &r.Status, &r.HoldID, &r.ExpiresAt)
	return r, err
}

type Worker struct {
	holds *holds.Repository
	store *Store
}

func NewWorker(h *holds.Repository, s *Store) *Worker { return &Worker{h, s} }

type Service struct {
	store     *Store
	publisher Publisher
	holds     *holds.Repository
}

func NewService(store *Store, publisher Publisher, repository ...*holds.Repository) *Service {
	s := &Service{store: store, publisher: publisher}
	if len(repository) > 0 {
		s.holds = repository[0]
	}
	return s
}
func (s *Service) Submit(ctx context.Context, ownerID, key, correlationID string, expiresAt time.Time, segments []holds.Segment) (Result, error) {
	if correlationID == "" {
		correlationID = holds.NewID()
	}
	c := HoldRequestedV1{RequestID: holds.NewID(), OwnerID: ownerID, IdempotencyKey: key, CorrelationID: correlationID, ExpiresAt: expiresAt, Segments: segments}
	if _, err := c.ShardKey(); err != nil {
		return Result{}, err
	}
	r, created, err := s.store.Begin(ctx, c)
	if err != nil || !created {
		return r, err
	}
	if err = s.publisher.Publish(ctx, c); err != nil {
		_ = s.store.Delete(ctx, c.RequestID)
		return Result{}, err
	}
	return r, nil
}
func (s *Service) Get(ctx context.Context, id string) (Result, error) { return s.store.Get(ctx, id) }
func (s *Service) GetHold(ctx context.Context, id string) (holds.Hold, error) {
	if s.holds == nil {
		return holds.Hold{}, errors.New("hold lookup unavailable")
	}
	return s.holds.Get(ctx, id)
}
func (w *Worker) Handle(ctx context.Context, c HoldRequestedV1) error {
	keys, err := c.ShardKeys()
	if err != nil {
		return err
	}
	if len(keys) > 1 {
		return w.handleSaga(ctx, c, keys)
	}
	holdID := holds.NewID()
	err = w.holds.Create(ctx, holds.CreateCommand{HoldID: holdID, OwnerID: c.OwnerID, CorrelationID: c.CorrelationID, CausationID: c.RequestID, ExpiresAt: c.ExpiresAt, Segments: c.Segments})
	if err != nil {
		_, e := w.store.pool.Exec(ctx, `UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$1`, c.RequestID)
		return e
	}
	_, err = w.store.pool.Exec(ctx, `UPDATE booking.hold_requests SET status='HELD',hold_id=$2,expires_at=$3 WHERE id=$1`, c.RequestID, holdID, c.ExpiresAt.UTC())
	return err
}

func (w *Worker) handleSaga(ctx context.Context, c HoldRequestedV1, keys []string) error {
	sagaID := holds.NewID()
	if _, err := w.store.pool.Exec(ctx, `INSERT INTO booking.hold_sagas(id,request_id,state,mapping_version) VALUES($1,$2,'PREPARING','v1')`, sagaID, c.RequestID); err != nil {
		return err
	}
	groups := make(map[string][]holds.Segment, len(keys))
	for _, segment := range c.Segments {
		key := segment.TripID + ":" + segment.CarriageID
		groups[key] = append(groups[key], segment)
	}
	created := make(map[string]string, len(groups))
	for _, key := range keys {
		if _, err := w.store.pool.Exec(ctx, `INSERT INTO booking.hold_saga_steps(saga_id,shard_key,state) VALUES($1,$2,'PENDING')`, sagaID, key); err != nil {
			return err
		}
		holdID := holds.NewID()
		err := w.holds.Create(ctx, holds.CreateCommand{HoldID: holdID, OwnerID: c.OwnerID, CorrelationID: c.CorrelationID, CausationID: c.RequestID, ExpiresAt: c.ExpiresAt, Segments: groups[key]})
		if err != nil {
			_, _ = w.store.pool.Exec(ctx, `UPDATE booking.hold_sagas SET state='COMPENSATING' WHERE id=$1`, sagaID)
			for _, prior := range created {
				if cancelErr := w.holds.Cancel(ctx, prior, c.CorrelationID, sagaID, time.Now().UTC()); cancelErr != nil {
					return fmt.Errorf("compensate saga %s: %w", sagaID, cancelErr)
				}
			}
			_, _ = w.store.pool.Exec(ctx, `UPDATE booking.hold_saga_steps SET state='COMPENSATED' WHERE saga_id=$1 AND state='HELD'; UPDATE booking.hold_saga_steps SET state='REJECTED' WHERE saga_id=$1 AND shard_key=$2; UPDATE booking.hold_sagas SET state='COMPENSATED',completed_at=now() WHERE id=$1; UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$3`, sagaID, key, c.RequestID)
			return nil
		}
		created[key] = holdID
		if _, err := w.store.pool.Exec(ctx, `UPDATE booking.hold_saga_steps SET state='HELD',hold_id=$3 WHERE saga_id=$1 AND shard_key=$2`, sagaID, key, holdID); err != nil {
			return err
		}
	}
	_, err := w.store.pool.Exec(ctx, `UPDATE booking.hold_sagas SET state='HELD',completed_at=now() WHERE id=$1; UPDATE booking.hold_requests SET status='HELD',expires_at=$2 WHERE id=$3`, sagaID, c.ExpiresAt.UTC(), c.RequestID)
	return err
}
