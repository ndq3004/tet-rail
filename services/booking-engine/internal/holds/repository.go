package holds

import (
	"context"
	"crypto/rand"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"sort"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
)

//go:embed migrations/*.sql
var migrationFiles embed.FS

var ErrUnavailable = errors.New("one or more seat segments are unavailable")
var ErrHoldExpired = errors.New("hold has expired")
var ErrHoldNotActive = errors.New("hold is not active")

type Segment struct {
	TripID, SeatID string
	Index          int
}
type CreateCommand struct {
	HoldID, OwnerID, CorrelationID, CausationID string
	ExpiresAt                                   time.Time
	Segments                                    []Segment
}

type Repository struct{ pool *pgxpool.Pool }

func NewRepository(pool *pgxpool.Pool) *Repository { return &Repository{pool: pool} }

type OutboxEvent struct {
	ID      string
	Payload events.SeatStateEventV1
}

func RunMigrations(ctx context.Context, pool *pgxpool.Pool) error {
	sql, err := migrationFiles.ReadFile("migrations/001_hold_lifecycle.sql")
	if err != nil {
		return err
	}
	_, err = pool.Exec(ctx, string(sql))
	return err
}

func (r *Repository) Create(ctx context.Context, command CreateCommand) error {
	if err := validateCreate(command); err != nil {
		return err
	}
	return r.inTx(ctx, func(tx pgx.Tx) error {
		segments := normalized(command.Segments)
		if err := lockSegments(ctx, tx, segments); err != nil {
			return err
		}
		for _, segment := range segments {
			var blocked bool
			err := tx.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM booking.held_segments WHERE trip_id=$1 AND seat_id=$2 AND segment_index=$3 UNION ALL SELECT 1 FROM booking.confirmed_allocations WHERE trip_id=$1 AND seat_id=$2 AND segment_index=$3)`, segment.TripID, segment.SeatID, segment.Index).Scan(&blocked)
			if err != nil {
				return err
			}
			if blocked {
				return ErrUnavailable
			}
		}
		if _, err := tx.Exec(ctx, `INSERT INTO booking.holds(id, owner_id, state, expires_at) VALUES ($1,$2,'ACTIVE',$3)`, command.HoldID, command.OwnerID, command.ExpiresAt.UTC()); err != nil {
			return err
		}
		for _, segment := range segments {
			if _, err := tx.Exec(ctx, `INSERT INTO booking.held_segments(hold_id,trip_id,seat_id,segment_index) VALUES ($1,$2,$3,$4)`, command.HoldID, segment.TripID, segment.SeatID, segment.Index); err != nil {
				return err
			}
		}
		return r.enqueueSeatEvents(ctx, tx, events.SeatHeldV1, command.CorrelationID, command.CausationID, segments)
	})
}

func (r *Repository) ClaimOutbox(ctx context.Context, lease time.Duration) (*OutboxEvent, error) {
	var id string
	var payload []byte
	err := r.pool.QueryRow(ctx, `UPDATE booking.outbox_events SET locked_until=now()+$1::interval, attempts=attempts+1 WHERE id=(SELECT id FROM booking.outbox_events WHERE published_at IS NULL AND available_at <= now() AND (locked_until IS NULL OR locked_until < now()) ORDER BY occurred_at FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING id::text,payload`, lease.String()).Scan(&id, &payload)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var event events.SeatStateEventV1
	if err := json.Unmarshal(payload, &event); err != nil {
		return nil, fmt.Errorf("decode outbox event %s: %w", id, err)
	}
	return &OutboxEvent{ID: id, Payload: event}, nil
}
func (r *Repository) MarkPublished(ctx context.Context, id string) error {
	_, err := r.pool.Exec(ctx, `UPDATE booking.outbox_events SET published_at=now(),locked_until=NULL WHERE id=$1`, id)
	return err
}
func (r *Repository) ReleaseOutbox(ctx context.Context, id string, retryAfter time.Duration) error {
	_, err := r.pool.Exec(ctx, `UPDATE booking.outbox_events SET locked_until=NULL,available_at=now()+$2::interval WHERE id=$1`, id, retryAfter.String())
	return err
}

func (r *Repository) Confirm(ctx context.Context, holdID, correlationID, causationID string, now time.Time) error {
	return r.transition(ctx, holdID, correlationID, causationID, now, true)
}
func (r *Repository) ExpireDue(ctx context.Context, now time.Time, limit int) (int, error) {
	if limit < 1 {
		return 0, fmt.Errorf("limit must be positive")
	}
	rows, err := r.pool.Query(ctx, `SELECT id FROM booking.holds WHERE state='ACTIVE' AND expires_at <= $1 ORDER BY expires_at LIMIT $2`, now.UTC(), limit)
	if err != nil {
		return 0, err
	}
	defer rows.Close()
	var ids []string
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			return 0, err
		}
		ids = append(ids, id)
	}
	if err := rows.Err(); err != nil {
		return 0, err
	}
	count := 0
	for _, id := range ids {
		if err := r.transition(ctx, id, "00000000-0000-0000-0000-000000000000", id, now, false); err == nil {
			count++
		} else if !errors.Is(err, ErrHoldNotActive) {
			return count, err
		}
	}
	return count, nil
}

func (r *Repository) transition(ctx context.Context, holdID, correlationID, causationID string, now time.Time, confirm bool) error {
	expiredOnConfirm := false
	err := r.inTx(ctx, func(tx pgx.Tx) error {
		var state string
		var expiresAt time.Time
		if err := tx.QueryRow(ctx, `SELECT state,expires_at FROM booking.holds WHERE id=$1 FOR UPDATE`, holdID).Scan(&state, &expiresAt); err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				return ErrHoldNotActive
			}
			return err
		}
		if state != "ACTIVE" {
			return ErrHoldNotActive
		}
		rows, err := tx.Query(ctx, `SELECT trip_id::text,seat_id::text,segment_index FROM booking.held_segments WHERE hold_id=$1`, holdID)
		if err != nil {
			return err
		}
		var segments []Segment
		for rows.Next() {
			var s Segment
			if err := rows.Scan(&s.TripID, &s.SeatID, &s.Index); err != nil {
				rows.Close()
				return err
			}
			segments = append(segments, s)
		}
		rows.Close()
		if err := lockSegments(ctx, tx, normalized(segments)); err != nil {
			return err
		}
		expired := !now.UTC().Before(expiresAt.UTC())
		if confirm && !expired {
			for _, s := range segments {
				if _, err := tx.Exec(ctx, `INSERT INTO booking.confirmed_allocations(trip_id,seat_id,segment_index,hold_id) VALUES($1,$2,$3,$4)`, s.TripID, s.SeatID, s.Index, holdID); err != nil {
					return err
				}
			}
			if _, err := tx.Exec(ctx, `UPDATE booking.holds SET state='CONFIRMED', confirmed_at=$2 WHERE id=$1`, holdID, now.UTC()); err != nil {
				return err
			}
			if _, err := tx.Exec(ctx, `DELETE FROM booking.held_segments WHERE hold_id=$1`, holdID); err != nil {
				return err
			}
			return r.enqueueSeatEvents(ctx, tx, events.HoldConfirmedV1, correlationID, causationID, segments)
		}
		if _, err := tx.Exec(ctx, `UPDATE booking.holds SET state='EXPIRED', expired_at=$2 WHERE id=$1`, holdID, now.UTC()); err != nil {
			return err
		}
		if _, err := tx.Exec(ctx, `DELETE FROM booking.held_segments WHERE hold_id=$1`, holdID); err != nil {
			return err
		}
		if err := r.enqueueSeatEvents(ctx, tx, events.HoldExpiredV1, correlationID, causationID, segments); err != nil {
			return err
		}
		if confirm {
			expiredOnConfirm = true
		}
		return nil
	})
	if err != nil {
		return err
	}
	if expiredOnConfirm {
		return ErrHoldExpired
	}
	return nil
}

func (r *Repository) enqueueSeatEvents(ctx context.Context, tx pgx.Tx, eventType, correlationID, causationID string, segments []Segment) error {
	bySeat := map[string][]int{}
	tripBySeat := map[string]string{}
	for _, s := range segments {
		key := s.TripID + ":" + s.SeatID
		tripBySeat[key] = s.TripID
		bySeat[key] = append(bySeat[key], s.Index)
	}
	for key, indices := range bySeat {
		sort.Ints(indices)
		event := events.SeatStateEventV1{EventID: newID(), EventType: eventType, Version: 1, OccurredAt: time.Now().UTC(), CorrelationID: correlationID, CausationID: causationID, Payload: events.SeatStatePayload{TripID: tripBySeat[key], SeatID: key[len(tripBySeat[key])+1:], SegmentIndices: indices}}
		body, err := json.Marshal(event)
		if err != nil {
			return err
		}
		if _, err = tx.Exec(ctx, `INSERT INTO booking.outbox_events(id,event_type,event_key,payload) VALUES($1,$2,$3,$4)`, event.EventID, event.EventType, key, body); err != nil {
			return err
		}
	}
	return nil
}

func (r *Repository) inTx(ctx context.Context, fn func(pgx.Tx) error) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if err = fn(tx); err != nil {
		return err
	}
	return tx.Commit(ctx)
}
func lockSegments(ctx context.Context, tx pgx.Tx, segments []Segment) error {
	for _, s := range segments {
		if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtextextended($1,0))`, s.TripID+":"+s.SeatID+fmt.Sprintf(":%d", s.Index)); err != nil {
			return err
		}
	}
	return nil
}
func normalized(in []Segment) []Segment {
	out := append([]Segment(nil), in...)
	sort.Slice(out, func(i, j int) bool {
		return out[i].TripID+out[i].SeatID+fmt.Sprint(out[i].Index) < out[j].TripID+out[j].SeatID+fmt.Sprint(out[j].Index)
	})
	return out
}
func validateCreate(c CreateCommand) error {
	if c.HoldID == "" || c.OwnerID == "" || c.CorrelationID == "" || c.CausationID == "" || c.ExpiresAt.IsZero() || len(c.Segments) == 0 {
		return fmt.Errorf("hold command is incomplete")
	}
	seen := map[string]bool{}
	for _, s := range c.Segments {
		if s.TripID == "" || s.SeatID == "" || s.Index < 1 {
			return fmt.Errorf("invalid held segment")
		}
		key := s.TripID + s.SeatID + fmt.Sprint(s.Index)
		if seen[key] {
			return fmt.Errorf("duplicate held segment")
		}
		seen[key] = true
	}
	return nil
}
func newID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%s-%s-%s-%s-%s", hex.EncodeToString(b[0:4]), hex.EncodeToString(b[4:6]), hex.EncodeToString(b[6:8]), hex.EncodeToString(b[8:10]), hex.EncodeToString(b[10:]))
}
