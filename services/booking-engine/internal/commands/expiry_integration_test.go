//go:build integration

package commands

import (
	"context"
	"encoding/json"
	"sync"
	"testing"
	"time"

	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

func TestExpiryRestartAndPaymentRace(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	repository, store := holds.NewRepository(pool), NewStore(pool)
	worker := NewWorker(repository, store)
	trip, owner := holds.NewID(), holds.NewID()

	makeHold := func(carriages ...string) (string, []holds.Segment) {
		t.Helper()
		command := HoldRequestedV1{
			RequestID: holds.NewID(), OwnerID: owner, IdempotencyKey: holds.NewID(),
			CorrelationID: holds.NewID(), ExpiresAt: time.Now().Add(time.Minute),
		}
		for _, carriage := range carriages {
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
			t.Fatalf("create hold: %v %v", result.Status, err)
		}
		return result.HoldID, command.Segments
	}

	logical, seats := makeHold("A", "B")
	standalone, physical := makeHold("A")
	confirmed, sold := makeHold("A", "B")
	confirm := func(id string) (string, error) {
		event := holds.PaymentEvent{
			EventID: holds.NewID(), CorrelationID: holds.NewID(),
			Payload: holds.PaymentPayload{OrderID: holds.NewID(), TransactionID: holds.NewID(),
				HoldID: id, OwnerID: owner, Deadline: time.Now().Add(time.Minute)},
		}
		body, err := repository.ConfirmPayment(ctx, event)
		if err != nil {
			return "", err
		}
		var result holds.PaymentEvent
		err = json.Unmarshal(body, &result)
		return result.EventType, err
	}
	if result, err := confirm(confirmed); err != nil || result != "BookingConfirmed.v1" {
		t.Fatalf("confirm: %s %v", result, err)
	}

	if _, err := pool.Exec(ctx, `UPDATE booking.holds SET expires_at=now() WHERE owner_id=$1;
	`, owner); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE booking.hold_sagas SET expires_at=now() WHERE owner_id=$1`, owner); err != nil {
		t.Fatal(err)
	}

	// New repository instances recover overdue rows without process or Redis state.
	var workers sync.WaitGroup
	for range 4 {
		workers.Add(1)
		go func() {
			defer workers.Done()
			if _, err := holds.NewRepository(pool).ExpireDue(ctx, time.Now(), 100); err != nil {
				t.Error(err)
			}
		}()
	}
	workers.Add(1)
	go func() {
		defer workers.Done()
		if result, err := confirm(logical); err != nil || result != "BookingConfirmationFailed.v1" {
			t.Errorf("late confirmation: %s %v", result, err)
		}
	}()
	workers.Wait()

	for id, expected := range map[string]string{logical: "EXPIRED", standalone: "EXPIRED", confirmed: "CONFIRMED"} {
		hold, err := repository.Get(ctx, id)
		if err != nil || hold.State != expected {
			t.Fatalf("hold state=%s, want %s: %v", hold.State, expected, err)
		}
	}
	released := append(seats, physical...)
	if err := repository.Create(ctx, holds.CreateCommand{
		HoldID: holds.NewID(), OwnerID: owner, CorrelationID: holds.NewID(), CausationID: holds.NewID(),
		ExpiresAt: time.Now().Add(time.Minute), Segments: released,
	}); err != nil {
		t.Fatalf("rebook released segments: %v", err)
	}
	for _, seat := range sold {
		occupied, err := repository.Occupied(ctx, seat)
		if err != nil || !occupied {
			t.Fatalf("confirmed segment no longer occupied: %v", err)
		}
	}
	for _, seat := range released {
		rows, err := pool.Query(ctx, `SELECT event_type FROM booking.outbox_events
			WHERE event_key=$1 ORDER BY occurred_at,id`, trip+":"+seat.SeatID)
		if err != nil {
			t.Fatal(err)
		}
		var types []string
		for rows.Next() {
			var value string
			if err := rows.Scan(&value); err != nil {
				rows.Close()
				t.Fatal(err)
			}
			types = append(types, value)
		}
		if len(types) != 3 || types[0] != events.SeatHeldV1 || types[1] != events.HoldExpiredV1 || types[2] != events.SeatHeldV1 || rows.Err() != nil {
			t.Fatalf("unexpected seat events: %v", types)
		}
	}
}

func TestSeatOutboxKeepsReleaseAheadOfReholdDuringRetry(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	repo := holds.NewRepository(pool)
	trip, seat := holds.NewID(), holds.NewID()
	first, second := holds.NewID(), holds.NewID()
	start := time.Now().UTC().Add(-time.Hour)
	for i, id := range []string{first, second} {
		kind := []string{events.HoldExpiredV1, events.SeatHeldV1}[i]
		event, err := json.Marshal(events.SeatStateEventV1{
			EventID: id, EventType: kind, Version: 1, OccurredAt: start,
			CorrelationID: id, CausationID: id,
			Payload: events.SeatStatePayload{TripID: trip, SeatID: seat, SegmentIndices: []int{1}},
		})
		if err != nil {
			t.Fatal(err)
		}
		if _, err := pool.Exec(ctx, `INSERT INTO booking.outbox_events(id,event_type,event_key,payload,occurred_at)
			VALUES ($1,$2,$3,$4,$5)`, id, kind, trip+":"+seat, event, start.Add(time.Duration(i)*time.Second)); err != nil {
			t.Fatal(err)
		}
	}
	event, err := repo.ClaimOutbox(ctx, time.Minute)
	if err != nil || event == nil || event.ID != first {
		t.Fatalf("claim expiry first: %v %v", event, err)
	}
	assertBlocked := func() {
		t.Helper()
		event, err := repo.ClaimOutbox(ctx, time.Minute)
		if err != nil || (event != nil && event.ID == second) {
			t.Fatalf("rehold bypassed expiry: %v %v", event, err)
		}
		if event != nil {
			if err := repo.ReleaseOutbox(ctx, event.ID, 0); err != nil {
				t.Fatal(err)
			}
		}
	}
	assertBlocked()
	if err := repo.ReleaseOutbox(ctx, first, time.Minute); err != nil {
		t.Fatal(err)
	}
	assertBlocked()
	if err := repo.MarkPublished(ctx, first); err != nil {
		t.Fatal(err)
	}
	event, err = repo.ClaimOutbox(ctx, time.Minute)
	if err != nil || event == nil || event.ID != second {
		t.Fatalf("claim rehold after expiry: %v %v", event, err)
	}
	if err := repo.MarkPublished(ctx, second); err != nil {
		t.Fatal(err)
	}
}
