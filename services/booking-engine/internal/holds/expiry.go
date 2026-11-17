package holds

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"sync/atomic"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
)

func (r *Repository) ExpireDue(ctx context.Context, now time.Time, limit int) (int, error) {
	if limit < 1 {
		return 0, fmt.Errorf("limit must be positive")
	}

	// Alternate between standalone and multi-carriage holds to avoid starving either queue.
	count := 0
	empty := [2]bool{}
	for turn := 0; count < limit && !(empty[0] && empty[1]); turn++ {
		queue := turn % 2
		if empty[queue] {
			continue
		}
		expired, err := r.expireOne(ctx, now.UTC(), queue == 1)
		if err != nil {
			return count, err
		}
		if expired {
			count++
		} else {
			empty[queue] = true
		}
	}
	return count, nil
}

func (r *Repository) expireOne(ctx context.Context, now time.Time, saga bool) (bool, error) {
	var claimed bool
	err := r.inTx(ctx, func(tx pgx.Tx) error {
		query := `
			SELECT id::text FROM booking.holds h
			WHERE state='ACTIVE' AND expires_at <= $1
			  AND NOT EXISTS (SELECT 1 FROM booking.hold_saga_steps WHERE hold_id=h.id)
			ORDER BY expires_at,id LIMIT 1 FOR UPDATE OF h SKIP LOCKED`
		if saga {
			query = `
				SELECT id::text FROM booking.hold_sagas
				WHERE state='HELD' AND expires_at <= $1
				ORDER BY expires_at,id LIMIT 1 FOR UPDATE SKIP LOCKED`
		}
		var id string
		if err := tx.QueryRow(ctx, query, now).Scan(&id); errors.Is(err, pgx.ErrNoRows) {
			return nil
		} else if err != nil {
			return err
		}
		claimed = true
		ids := []string{id}
		if saga {
			rows, err := tx.Query(ctx, `
				SELECT h.id::text FROM booking.holds h
				JOIN booking.hold_saga_steps s ON s.hold_id=h.id
				WHERE s.saga_id=$1 AND h.state='ACTIVE'
				ORDER BY h.id FOR UPDATE OF h`, id)
			if err != nil {
				return err
			}
			ids, err = pgx.CollectRows(rows, pgx.RowTo[string])
			if err != nil {
				return err
			}
			// Older confirmations only updated physical holds.
			if _, err := tx.Exec(ctx, `
				UPDATE booking.hold_sagas SET state=CASE WHEN EXISTS (
					SELECT 1 FROM booking.hold_saga_steps s JOIN booking.holds h ON h.id=s.hold_id
					WHERE s.saga_id=$1 AND h.state='CONFIRMED'
				) THEN 'CONFIRMED' ELSE 'EXPIRED' END WHERE id=$1`, id); err != nil {
				return err
			}
		}

		rows, err := tx.Query(ctx, `
			SELECT trip_id::text,seat_id::text,segment_index
			FROM booking.held_segments WHERE hold_id IN (SELECT unnest($1::text[])::uuid)`, ids)
		if err != nil {
			return err
		}
		segments, err := pgx.CollectRows(rows, func(row pgx.CollectableRow) (Segment, error) {
			var segment Segment
			err := row.Scan(&segment.TripID, &segment.SeatID, &segment.Index)
			return segment, err
		})
		if err != nil {
			return err
		}

		if err := lockSegments(ctx, tx, normalized(segments)); err != nil {
			return err
		}
		if err := r.expireInTx(ctx, tx, ids, segments, now, id, id); err != nil {
			return err
		}
		return nil
	})
	return claimed && err == nil, err
}

func (r *Repository) expireInTx(ctx context.Context, tx pgx.Tx, ids []string, segments []Segment, now time.Time, correlationID, causationID string) error {
	if _, err := tx.Exec(ctx, `UPDATE booking.holds SET state='EXPIRED',expired_at=$2
		WHERE id IN (SELECT unnest($1::text[])::uuid) AND state='ACTIVE'`, ids, now); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM booking.held_segments
		WHERE hold_id IN (SELECT unnest($1::text[])::uuid)`, ids); err != nil {
		return err
	}
	return r.enqueueSeatEvents(ctx, tx, events.HoldExpiredV1, correlationID, causationID, segments)
}

type ExpiryWorker struct {
	reconcile  func(context.Context, time.Time, int) (int, error)
	reconciled atomic.Uint64
	failures   atomic.Uint64
}

func NewExpiryWorker(repository *Repository) *ExpiryWorker {
	return &ExpiryWorker{reconcile: repository.ExpireDue}
}

func (w *ExpiryWorker) Run(ctx context.Context, interval time.Duration, batchSize int) {
	for ctx.Err() == nil {
		attempt, cancel := context.WithTimeout(ctx, 10*time.Second)
		count, err := w.reconcile(attempt, time.Now(), batchSize)
		cancel()
		w.reconciled.Add(uint64(count))
		if err != nil && ctx.Err() == nil {
			w.failures.Add(1)
			slog.Warn("hold expiry reconciliation will retry")
		}
		if err == nil && count == batchSize {
			continue
		}
		select {
		case <-ctx.Done():
		case <-time.After(interval):
		}
	}
}

func (w *ExpiryWorker) ServeHTTP(response http.ResponseWriter, _ *http.Request) {
	response.Header().Set("Content-Type", "text/plain; version=0.0.4")
	fmt.Fprintf(response, "# HELP tetrail_booking_expiry_reconciled_total Committed due-hold reconciliations.\n"+
		"# TYPE tetrail_booking_expiry_reconciled_total counter\n"+
		"tetrail_booking_expiry_reconciled_total %d\n"+
		"# HELP tetrail_booking_expiry_errors_total Failed expiry reconciliation batches.\n"+
		"# TYPE tetrail_booking_expiry_errors_total counter\n"+
		"tetrail_booking_expiry_errors_total %d\n", w.reconciled.Load(), w.failures.Load())
}
