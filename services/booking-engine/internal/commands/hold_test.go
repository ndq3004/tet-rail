package commands

import (
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"testing"
)

func TestShardKeyRejectsCrossTripCommand(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", SeatID: "one", Index: 1}, {TripID: "another-trip", SeatID: "two", Index: 1}}}
	if _, err := c.ShardKey(); err == nil {
		t.Fatal("expected cross-shard command rejection")
	}
}
