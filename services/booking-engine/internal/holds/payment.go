package holds

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

const PaymentTopicV1 = "ordering.payments.v1"

type PaymentEvent struct {
	EventID       string         `json:"event_id"`
	EventType     string         `json:"event_type"`
	Version       int            `json:"version"`
	OccurredAt    time.Time      `json:"occurred_at"`
	CorrelationID string         `json:"correlation_id"`
	CausationID   string         `json:"causation_id"`
	Payload       PaymentPayload `json:"payload"`
}

type PaymentPayload struct {
	OrderID       string    `json:"order_id"`
	HoldID        string    `json:"hold_id"`
	OwnerID       string    `json:"owner_id"`
	TransactionID string    `json:"provider_transaction_id"`
	Deadline      time.Time `json:"payment_expires_at"`
}

func (r *Repository) ConfirmPayment(ctx context.Context, event PaymentEvent) ([]byte, error) {
	var body []byte
	p := event.Payload
	err := r.inTx(ctx, func(tx pgx.Tx) error {
		if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtextextended('booking:payment:' || $1::text,0))`, p.OrderID); err != nil {
			return err
		}
		err := tx.QueryRow(ctx, `SELECT result FROM booking.payment_confirmations WHERE order_id=$1 AND transaction_id=$2`, p.OrderID, p.TransactionID).Scan(&body)
		if !errors.Is(err, pgx.ErrNoRows) {
			return err
		}

		// A rejected allocation rolls back the entire logical hold, including seat events.
		savepoint, err := tx.Begin(ctx)
		if err != nil {
			return err
		}
		resultType := "BookingConfirmed.v1"
		err = r.confirmPaymentHold(ctx, savepoint, event)
		if err != nil {
			if rollbackErr := savepoint.Rollback(ctx); rollbackErr != nil {
				return rollbackErr
			}
			var pgError *pgconn.PgError
			if !errors.Is(err, ErrHoldNotActive) && !errors.Is(err, ErrHoldExpired) &&
				!(errors.As(err, &pgError) && pgError.Code == "23505" && pgError.ConstraintName == "confirmed_allocations_pkey") {
				return err
			}
			resultType = "BookingConfirmationFailed.v1"
		} else if err := savepoint.Commit(ctx); err != nil {
			return err
		}

		body, err = json.Marshal(PaymentEvent{
			EventID: NewID(), EventType: resultType, Version: 1, OccurredAt: time.Now().UTC(),
			CorrelationID: event.CorrelationID, CausationID: event.EventID, Payload: p,
		})
		if err != nil {
			return err
		}
		_, err = tx.Exec(ctx, `INSERT INTO booking.payment_confirmations(transaction_id,order_id,result) VALUES ($1,$2,$3)`, p.TransactionID, p.OrderID, body)
		return err
	})
	return body, err
}

func (r *Repository) confirmPaymentHold(ctx context.Context, tx pgx.Tx, event PaymentEvent) error {
	p := event.Payload
	var sagaID string
	err := tx.QueryRow(ctx, `SELECT id::text FROM booking.hold_sagas
		WHERE id=$1 AND owner_id=$2 AND state='HELD' FOR UPDATE`, p.HoldID, p.OwnerID).Scan(&sagaID)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}

	rows, err := tx.Query(ctx, `
		SELECT h.id::text, h.state, h.expires_at
		FROM booking.holds h
		WHERE h.owner_id=$2 AND (h.id=$1 OR h.id IN (
			SELECT step.hold_id FROM booking.hold_saga_steps step
			JOIN booking.hold_sagas saga ON saga.id=step.saga_id
			WHERE saga.id=$1 AND saga.owner_id=$2 AND saga.state='HELD' AND step.state='HELD'
		))
		ORDER BY h.id FOR UPDATE OF h`, p.HoldID, p.OwnerID)
	if err != nil {
		return err
	}
	var ids []string
	expires := p.Deadline
	for rows.Next() {
		var id, state string
		var deadline time.Time
		if err := rows.Scan(&id, &state, &deadline); err != nil {
			rows.Close()
			return err
		}
		if state != "ACTIVE" {
			rows.Close()
			return ErrHoldNotActive
		}
		if deadline.Before(expires) {
			expires = deadline
		}
		ids = append(ids, id)
	}
	if err := rows.Err(); err != nil {
		return err
	}
	if len(ids) == 0 {
		return ErrHoldNotActive
	}

	rows, err = tx.Query(ctx, `
		SELECT trip_id::text,seat_id::text,segment_index
		FROM booking.held_segments WHERE hold_id::text=ANY($1)`, ids)
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
	if err := rows.Err(); err != nil {
		return err
	}
	if len(segments) < len(ids) {
		return ErrHoldNotActive
	}
	if err := lockSegments(ctx, tx, normalized(segments)); err != nil {
		return err
	}

	now := time.Now().UTC()
	if !now.Before(expires) {
		return ErrHoldExpired
	}
	_, err = tx.Exec(ctx, `
		INSERT INTO booking.confirmed_allocations(trip_id,seat_id,segment_index,hold_id,confirmed_at)
		SELECT trip_id,seat_id,segment_index,hold_id,$2 FROM booking.held_segments WHERE hold_id::text=ANY($1);
	`, ids, now)
	if err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `UPDATE booking.holds SET state='CONFIRMED',confirmed_at=$2 WHERE id::text=ANY($1)`, ids, now); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM booking.held_segments WHERE hold_id::text=ANY($1)`, ids); err != nil {
		return err
	}
	if sagaID != "" {
		if _, err := tx.Exec(ctx, `UPDATE booking.hold_sagas SET state='CONFIRMED' WHERE id=$1`, sagaID); err != nil {
			return err
		}
	}
	return r.enqueueSeatEvents(ctx, tx, "HoldConfirmed.v1", event.CorrelationID, event.EventID, segments)
}
