//go:build integration

package holds

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

func TestPostgresLifecycleWritesOutboxInSameTransaction(t *testing.T) {
	url := os.Getenv("BOOKING_TEST_DATABASE_URL")
	if url == "" {
		url = "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail?search_path=booking"
	}
	ctx := context.Background()
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := RunMigrations(ctx, pool); err != nil {
		t.Fatal(err)
	}
	repository := NewRepository(pool)
	now := time.Now().UTC()
	holdID := newID()
	tripID := newID()
	seatID := newID()
	command := CreateCommand{HoldID: holdID, OwnerID: newID(), CorrelationID: newID(), CausationID: newID(), ExpiresAt: now.Add(time.Minute), Segments: []Segment{{TripID: tripID, SeatID: seatID, Index: 1}, {TripID: tripID, SeatID: seatID, Index: 2}}}
	if err := repository.Create(ctx, command); err != nil {
		t.Fatal(err)
	}
	var held, outbox int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.held_segments WHERE hold_id=$1`, holdID).Scan(&held); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.outbox_events WHERE event_type='SeatHeld.v1' AND published_at IS NULL`).Scan(&outbox); err != nil {
		t.Fatal(err)
	}
	if held != 2 || outbox < 1 {
		t.Fatalf("held=%d outbox=%d, want 2 and at least 1", held, outbox)
	}
	if err := repository.Confirm(ctx, holdID, newID(), newID(), now); err != nil {
		t.Fatal(err)
	}
	var confirmed int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.confirmed_allocations WHERE hold_id=$1`, holdID).Scan(&confirmed); err != nil {
		t.Fatal(err)
	}
	if confirmed != 2 {
		t.Fatalf("confirmed=%d, want 2", confirmed)
	}
	expiredHoldID := newID()
	if err := repository.Create(ctx, CreateCommand{HoldID: expiredHoldID, OwnerID: newID(), CorrelationID: newID(), CausationID: newID(), ExpiresAt: now.Add(-time.Minute), Segments: []Segment{{TripID: newID(), SeatID: newID(), Index: 1}}}); err != nil {
		t.Fatal(err)
	}
	expired, err := repository.ExpireDue(ctx, now, 10)
	if err != nil || expired < 1 {
		t.Fatalf("ExpireDue() = %d, %v", expired, err)
	}
	var state string
	if err := pool.QueryRow(ctx, `SELECT state FROM booking.holds WHERE id=$1`, expiredHoldID).Scan(&state); err != nil {
		t.Fatal(err)
	}
	if state != "EXPIRED" {
		t.Fatalf("state=%s, want EXPIRED", state)
	}
}
