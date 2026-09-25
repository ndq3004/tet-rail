//go:build integration

package commands

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

func TestCrossCarriageSagaCreatesLogicalHoldAndCompensatesConflict(t *testing.T) {
	ctx := context.Background()
	url := os.Getenv("BOOKING_TEST_DATABASE_URL")
	if url == "" {
		url = "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail?search_path=booking"
	}
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := holds.RunMigrations(ctx, pool); err != nil {
		t.Fatal(err)
	}

	repository := holds.NewRepository(pool)
	store := NewStore(pool)
	worker := NewWorker(repository, store)
	now := time.Now().UTC()
	tripID := holds.NewID()
	command := HoldRequestedV1{
		RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(), CorrelationID: holds.NewID(), ExpiresAt: now.Add(time.Minute),
		Segments: []holds.Segment{{TripID: tripID, CarriageID: "A", SeatID: holds.NewID(), Index: 1}, {TripID: tripID, CarriageID: "B", SeatID: holds.NewID(), Index: 1}},
	}
	if _, created, err := store.Begin(ctx, command); err != nil || !created {
		t.Fatalf("Begin() = created:%v err:%v", created, err)
	}
	if err := worker.Handle(ctx, command); err != nil {
		t.Fatal(err)
	}
	result, err := store.Get(ctx, command.RequestID)
	if err != nil || result.Status != "HELD" || result.HoldID == "" || result.ExpiresAt == nil {
		t.Fatalf("result = %#v, err = %v", result, err)
	}
	logicalHold, err := repository.Get(ctx, result.HoldID)
	if err != nil || logicalHold.State != "HELD" || logicalHold.OwnerID != command.OwnerID {
		t.Fatalf("logical hold = %#v, err = %v", logicalHold, err)
	}
	if err := worker.Handle(ctx, command); err != nil {
		t.Fatalf("duplicate worker delivery returned %v", err)
	}

	compensatedSeat := holds.NewID()
	conflicting := HoldRequestedV1{
		RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(), CorrelationID: holds.NewID(), ExpiresAt: now.Add(time.Minute),
		Segments: []holds.Segment{{TripID: tripID, CarriageID: "A", SeatID: compensatedSeat, Index: 1}, {TripID: tripID, CarriageID: "B", SeatID: command.Segments[1].SeatID, Index: 1}},
	}
	if _, created, err := store.Begin(ctx, conflicting); err != nil || !created {
		t.Fatalf("Begin(conflicting) = created:%v err:%v", created, err)
	}
	if err := worker.Handle(ctx, conflicting); err != nil {
		t.Fatal(err)
	}
	result, err = store.Get(ctx, conflicting.RequestID)
	if err != nil || result.Status != "REJECTED" {
		t.Fatalf("conflicting result = %#v, err = %v", result, err)
	}
	var held int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.held_segments WHERE trip_id=$1 AND seat_id=$2`, tripID, compensatedSeat).Scan(&held); err != nil {
		t.Fatal(err)
	}
	if held != 0 {
		t.Fatalf("compensation left %d held segments", held)
	}
}
