package commands

import (
	"context"
	"encoding/json"
	"errors"
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
}

func NewService(store *Store, publisher Publisher) *Service { return &Service{store, publisher} }
func (s *Service) Submit(ctx context.Context, ownerID, key, correlationID string, expiresAt time.Time, segments []holds.Segment) (Result, error) {
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
func (w *Worker) Handle(ctx context.Context, c HoldRequestedV1) error {
	holdID := holds.NewID()
	err := w.holds.Create(ctx, holds.CreateCommand{HoldID: holdID, OwnerID: c.OwnerID, CorrelationID: c.CorrelationID, CausationID: c.RequestID, ExpiresAt: c.ExpiresAt, Segments: c.Segments})
	if err != nil {
		_, e := w.store.pool.Exec(ctx, `UPDATE booking.hold_requests SET status='REJECTED' WHERE id=$1`, c.RequestID)
		return e
	}
	_, err = w.store.pool.Exec(ctx, `UPDATE booking.hold_requests SET status='HELD',hold_id=$2,expires_at=$3 WHERE id=$1`, c.RequestID, holdID, c.ExpiresAt.UTC())
	return err
}
