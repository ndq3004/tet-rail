package commands

import (
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"testing"
)

func TestShardKeyRejectsCrossTripCommand(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", CarriageID: "A", SeatID: "one", Index: 1}, {TripID: "another-trip", CarriageID: "A", SeatID: "two", Index: 1}}}
	if _, err := c.ShardKey(); err == nil {
		t.Fatal("expected cross-shard command rejection")
	}
}

func TestShardKeysGroupsCarriages(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", CarriageID: "A", SeatID: "one", Index: 1}, {TripID: "trip", CarriageID: "B", SeatID: "two", Index: 1}}}
	keys, err := c.ShardKeys()
	if err != nil || len(keys) != 2 {
		t.Fatalf("ShardKeys() = %v, %v", keys, err)
	}
}

func TestShardKeysAreDeterministicallySorted(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", CarriageID: "B", SeatID: "one", Index: 1}, {TripID: "trip", CarriageID: "A", SeatID: "two", Index: 1}}}
	keys, err := c.ShardKeys()
	if err != nil || len(keys) != 2 || keys[0] != "trip:A" || keys[1] != "trip:B" {
		t.Fatalf("ShardKeys() = %v, %v", keys, err)
	}
}

func TestShardUsesTripAndCarriage(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", CarriageID: "A", SeatID: "one", Index: 1}}}
	shard, err := c.Shard()
	if err != nil || shard < 0 || shard >= MappingPartitionsV1 {
		t.Fatalf("Shard() = %d, %v", shard, err)
	}
}
