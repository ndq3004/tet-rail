package commands

import (
	"testing"

	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

func TestShardKeyRejectsCrossTripCommand(t *testing.T) {
	c := HoldRequestedV1{Segments: []holds.Segment{{TripID: "trip", CarriageID: "A", SeatID: "one", Index: 1}, {TripID: "another-trip", CarriageID: "A", SeatID: "two", Index: 1}}}
	if _, err := c.ShardKey(); err == nil {
		t.Fatal("expected cross-shard command rejection")
	}
}

func TestKafkaUsesUnsignedV1Partition(t *testing.T) {
	message := kafka.Message{Key: []byte("hello")}
	// FNV-1a("hello") is 0x4f9f2cab. The high-bit case below distinguishes the
	// unsigned v1 mapping from kafka.Hash's signed conversion.
	if got := (shardBalancer{}).Balance(message); got != 3 {
		t.Fatalf("partition = %d, want 3", got)
	}
	message.Key = []byte("a")
	if got := (shardBalancer{}).Balance(message); got != 4 {
		t.Fatalf("partition = %d, want 4", got)
	}
}

func BenchmarkShardRouting(b *testing.B) {
	key := []byte("40000000-0000-0000-0000-000000000001:A")
	for b.Loop() {
		_ = shardForKey(key)
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
