package commands

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

var ErrIdempotencyConflict = errors.New("idempotency key was used with a different request")

type Result struct {
	OwnerID   string     `json:"-"`
	RequestID string     `json:"request_id"`
	Status    string     `json:"status"`
	HoldID    string     `json:"hold_id,omitempty"`
	ExpiresAt *time.Time `json:"expires_at,omitempty"`
}
type Store struct{ pool *pgxpool.Pool }

func NewStore(pool *pgxpool.Pool) *Store { return &Store{pool} }
func (s *Store) Begin(ctx context.Context, command HoldRequestedV1) (Result, bool, error) {
	body, err := json.Marshal(command.Segments)
	if err != nil {
		return Result{}, false, err
	}
	result := Result{RequestID: command.RequestID, Status: "QUEUED"}
	envelope, err := json.Marshal(command)
	if err != nil {
		return Result{}, false, err
	}
	insert, err := s.pool.Exec(ctx, `INSERT INTO booking.hold_requests(id,owner_id,idempotency_key,payload,status,command_envelope)
		VALUES($1,$2,$3,$4,'QUEUED',$5) ON CONFLICT(owner_id,idempotency_key) DO NOTHING`,
		command.RequestID, command.OwnerID, command.IdempotencyKey, body, envelope)
	if err != nil {
		return Result{}, false, err
	}
	if insert.RowsAffected() == 1 {
		return result, true, nil
	}
	var payload []byte
	err = s.pool.QueryRow(ctx, `SELECT id::text,payload,status,COALESCE(hold_id::text,''),expires_at FROM booking.hold_requests WHERE owner_id=$1 AND idempotency_key=$2`, command.OwnerID, command.IdempotencyKey).Scan(&result.RequestID, &payload, &result.Status, &result.HoldID, &result.ExpiresAt)
	if err != nil {
		return Result{}, false, err
	}
	var previous []holds.Segment
	if err := json.Unmarshal(payload, &previous); err != nil {
		return Result{}, false, err
	}
	if !sameSegments(previous, command.Segments) {
		return Result{}, false, ErrIdempotencyConflict
	}
	return result, false, nil
}
func sameSegments(previous, submitted []holds.Segment) bool {
	if len(previous) != len(submitted) {
		return false
	}
	remaining := slices.Clone(previous)
	for _, segment := range submitted {
		index := slices.Index(remaining, segment)
		if index == -1 {
			return false
		}
		remaining = slices.Delete(remaining, index, index+1)
	}
	return true
}

func (s *Store) pendingCommand(ctx context.Context, id string, candidate HoldRequestedV1) (*HoldRequestedV1, error) {
	candidate.RequestID = id
	envelope, err := json.Marshal(candidate)
	if err != nil {
		return nil, err
	}
	var stored []byte
	err = s.pool.QueryRow(ctx, `UPDATE booking.hold_requests
		SET command_envelope=COALESCE(command_envelope,$2::jsonb)
		WHERE id=$1 AND status='QUEUED' AND acknowledged_at IS NULL
		RETURNING command_envelope`, id, envelope).Scan(&stored)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var command HoldRequestedV1
	if err := json.Unmarshal(stored, &command); err != nil {
		return nil, err
	}
	return &command, nil
}
func (s *Store) Get(ctx context.Context, id string) (Result, error) {
	var r Result
	err := s.pool.QueryRow(ctx, `SELECT id::text,status,COALESCE(hold_id::text,''),expires_at,owner_id::text FROM booking.hold_requests WHERE id=$1`, id).Scan(&r.RequestID, &r.Status, &r.HoldID, &r.ExpiresAt, &r.OwnerID)
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
	if _, err := c.ShardKeys(); err != nil {
		return Result{}, err
	}
	r, created, err := s.store.Begin(ctx, c)
	if err != nil {
		return Result{}, err
	}
	command := &c
	if !created {
		command, err = s.store.pendingCommand(ctx, r.RequestID, c)
		if err != nil || command == nil {
			return r, err
		}
	}
	if err := s.publisher.Publish(ctx, *command); err != nil {
		return Result{}, err
	}
	if _, err := s.store.pool.Exec(ctx, `UPDATE booking.hold_requests
		SET acknowledged_at=COALESCE(acknowledged_at,now()) WHERE id=$1`, r.RequestID); err != nil {
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
	return w.handle(ctx, c, 0, 0)
}

func (w *Worker) acquireEpoch(ctx context.Context, partition int) (int64, error) {
	var epoch int64
	err := w.store.pool.QueryRow(ctx, `INSERT INTO booking.shard_epochs(mapping_version,partition_id,epoch)
		VALUES($1,$2,1) ON CONFLICT(mapping_version,partition_id)
		DO UPDATE SET epoch=booking.shard_epochs.epoch+1 RETURNING epoch`, MappingVersionV1, partition).Scan(&epoch)
	return epoch, err
}

var errStaleOwner = errors.New("stale shard owner")

func (w *Worker) handle(ctx context.Context, c HoldRequestedV1, partition int, epoch int64) error {
	tx, err := w.store.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(context.Background())
	if epoch != 0 {
		var current int64
		err := tx.QueryRow(ctx, `SELECT epoch FROM booking.shard_epochs
			WHERE mapping_version=$1 AND partition_id=$2 FOR UPDATE`, MappingVersionV1, partition).Scan(&current)
		if err != nil {
			return err
		}
		if current != epoch {
			return errStaleOwner
		}
	}
	if err := w.handleInTx(ctx, tx, c); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (w *Worker) handleInTx(ctx context.Context, tx pgx.Tx, c HoldRequestedV1) error {
	var status string
	err := tx.QueryRow(ctx, `SELECT status FROM booking.hold_requests WHERE id=$1 FOR UPDATE`, c.RequestID).Scan(&status)
	if errors.Is(err, pgx.ErrNoRows) {
		// Older ingress could delete the request after an ambiguous Kafka publish.
		return nil
	}
	if err != nil {
		return err
	}
	if status != "QUEUED" {
		return nil
	}
	if !c.ExpiresAt.After(time.Now()) {
		_, err := tx.Exec(ctx, `UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$1`, c.RequestID)
		return err
	}
	keys, err := c.ShardKeys()
	if err != nil {
		return err
	}
	if len(keys) > 1 {
		return w.handleSaga(ctx, tx, c, keys)
	}
	holdID := holds.NewID()
	err = w.holds.CreateInTx(ctx, tx, holds.CreateCommand{HoldID: holdID, OwnerID: c.OwnerID, CorrelationID: c.CorrelationID, CausationID: c.RequestID, ExpiresAt: c.ExpiresAt, Segments: c.Segments})
	if errors.Is(err, holds.ErrUnavailable) {
		_, e := tx.Exec(ctx, `UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$1`, c.RequestID)
		return e
	}
	if err != nil {
		return err
	}
	_, err = tx.Exec(ctx, `UPDATE booking.hold_requests SET status='HELD',hold_id=$2,expires_at=$3 WHERE id=$1`, c.RequestID, holdID, c.ExpiresAt.UTC())
	return err
}

func (w *Worker) handleSaga(ctx context.Context, tx pgx.Tx, c HoldRequestedV1, keys []string) error {
	sagaID := holds.NewID()
	if _, err := tx.Exec(ctx, `INSERT INTO booking.hold_sagas(id,request_id,state,mapping_version,owner_id,expires_at) VALUES($1,$2,'PREPARING','v1',$3,$4)`, sagaID, c.RequestID, c.OwnerID, c.ExpiresAt.UTC()); err != nil {
		return err
	}
	groups := make(map[string][]holds.Segment, len(keys))
	for _, segment := range c.Segments {
		key := segment.TripID + ":" + segment.CarriageID
		groups[key] = append(groups[key], segment)
	}
	created := make(map[string]string, len(groups))
	for _, key := range keys {
		if _, err := tx.Exec(ctx, `INSERT INTO booking.hold_saga_steps(saga_id,shard_key,state) VALUES($1,$2,'PENDING')`, sagaID, key); err != nil {
			return err
		}
		holdID := holds.NewID()
		err := w.holds.CreateInTx(ctx, tx, holds.CreateCommand{HoldID: holdID, OwnerID: c.OwnerID, CorrelationID: c.CorrelationID, CausationID: c.RequestID, ExpiresAt: c.ExpiresAt, Segments: groups[key]})
		if errors.Is(err, holds.ErrUnavailable) {
			for _, prior := range created {
				if cancelErr := w.holds.CancelInTx(ctx, tx, prior, c.CorrelationID, sagaID); cancelErr != nil {
					return fmt.Errorf("compensate saga %s: %w", sagaID, cancelErr)
				}
			}
			if _, updateErr := tx.Exec(ctx, `UPDATE booking.hold_saga_steps SET state='COMPENSATED' WHERE saga_id=$1 AND state='HELD'`, sagaID); updateErr != nil {
				return updateErr
			}
			if _, updateErr := tx.Exec(ctx, `UPDATE booking.hold_saga_steps SET state='REJECTED' WHERE saga_id=$1 AND shard_key=$2`, sagaID, key); updateErr != nil {
				return updateErr
			}
			if _, updateErr := tx.Exec(ctx, `UPDATE booking.hold_sagas SET state='COMPENSATED',completed_at=now() WHERE id=$1`, sagaID); updateErr != nil {
				return updateErr
			}
			if _, updateErr := tx.Exec(ctx, `UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$1`, c.RequestID); updateErr != nil {
				return updateErr
			}
			return nil
		}
		if err != nil {
			return err
		}
		created[key] = holdID
		if _, err := tx.Exec(ctx, `UPDATE booking.hold_saga_steps SET state='HELD',hold_id=$3 WHERE saga_id=$1 AND shard_key=$2`, sagaID, key, holdID); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(ctx, `UPDATE booking.hold_sagas SET state='HELD',completed_at=now() WHERE id=$1`, sagaID); err != nil {
		return err
	}
	_, err := tx.Exec(ctx, `UPDATE booking.hold_requests SET status='HELD',hold_id=$1,expires_at=$2 WHERE id=$3`, sagaID, c.ExpiresAt.UTC(), c.RequestID)
	return err
}
