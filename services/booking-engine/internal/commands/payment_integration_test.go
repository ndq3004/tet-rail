//go:build integration

package commands

import (
	"bytes"
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

func TestPaymentConfirmationIsAtomicAndReplayable(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	repo, store := holds.NewRepository(pool), NewStore(pool)
	worker := NewWorker(repo, store)
	for _, test := range []struct {
		name           string
		late, conflict bool
		carriages      []string
		want           int
	}{
		{name: "physical hold", carriages: []string{"A"}, want: 1},
		{name: "logical hold", carriages: []string{"A", "B"}, want: 2},
		{name: "expired hold", late: true, carriages: []string{"A", "B"}},
		{name: "allocation conflict", conflict: true, carriages: []string{"A", "B"}},
	} {
		t.Run(test.name, func(t *testing.T) {
			command := HoldRequestedV1{
				RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(),
				CorrelationID: holds.NewID(), ExpiresAt: time.Now().Add(time.Minute),
			}
			trip := holds.NewID()
			for _, carriage := range test.carriages {
				command.Segments = append(command.Segments, holds.Segment{TripID: trip, CarriageID: carriage, SeatID: holds.NewID(), Index: 1})
			}
			if _, _, err := store.Begin(ctx, command); err != nil {
				t.Fatal(err)
			}
			if err := worker.Handle(ctx, command); err != nil {
				t.Fatal(err)
			}
			result, err := store.Get(ctx, command.RequestID)
			if err != nil || result.Status != "HELD" {
				t.Fatalf("hold creation failed: %v %v", result.Status, err)
			}
			if test.late {
				if _, err := pool.Exec(ctx, `UPDATE booking.holds SET expires_at=now()-interval '1 second' WHERE owner_id=$1`, command.OwnerID); err != nil {
					t.Fatal(err)
				}
			}
			if test.conflict {
				if _, err := pool.Exec(ctx, `
					INSERT INTO booking.confirmed_allocations(trip_id,seat_id,segment_index,hold_id)
					SELECT trip_id,seat_id,segment_index,hold_id FROM booking.held_segments WHERE trip_id=$1 AND seat_id=$2
				`, trip, command.Segments[1].SeatID); err != nil {
					t.Fatal(err)
				}
			}
			event := holds.PaymentEvent{
				EventID: holds.NewID(), EventType: "PaymentAuthorized.v1", Version: 1, CorrelationID: command.CorrelationID,
				Payload: holds.PaymentPayload{OrderID: holds.NewID(), HoldID: result.HoldID, OwnerID: command.OwnerID,
					TransactionID: holds.NewID(), Deadline: command.ExpiresAt},
			}
			type delivery struct {
				body []byte
				err  error
			}
			replies := make(chan delivery, 4)
			for range 4 {
				go func() {
					body, err := repo.ConfirmPayment(ctx, event)
					replies <- delivery{body, err}
				}()
			}
			var first []byte
			for range 4 {
				reply := <-replies
				if reply.err != nil {
					t.Fatal(reply.err)
				}
				if first == nil {
					// Normalize JSONB formatting before comparing it with the original encoding.
					first = normalizedJSON(t, reply.body)
				}
				if !bytes.Equal(first, normalizedJSON(t, reply.body)) {
					t.Fatal("duplicate produced a different result")
				}
			}
			var response holds.PaymentEvent
			if err := json.Unmarshal(first, &response); err != nil {
				t.Fatal(err)
			}
			wantType := "BookingConfirmed.v1"
			if test.want == 0 {
				wantType = "BookingConfirmationFailed.v1"
			}
			if response.EventType != wantType || response.CausationID != event.EventID || response.Payload.TransactionID != event.Payload.TransactionID {
				t.Fatalf("unexpected response: %s", first)
			}
			var allocations int
			if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.confirmed_allocations WHERE trip_id=$1`, trip).Scan(&allocations); err != nil {
				t.Fatal(err)
			}
			if test.conflict {
				allocations--
			}
			if allocations != test.want {
				t.Fatalf("allocated %d segments, want %d", allocations, test.want)
			}
		})
	}
}

func normalizedJSON(t *testing.T, body []byte) []byte {
	t.Helper()
	var value any
	if err := json.Unmarshal(body, &value); err != nil {
		t.Fatal(err)
	}
	result, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return result
}
