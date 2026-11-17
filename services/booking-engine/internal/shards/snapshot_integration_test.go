//go:build integration

package shards

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/redis/go-redis/v9"
)

func TestRedisSnapshotRoundTrip(t *testing.T) {
	address := os.Getenv("BOOKING_TEST_REDIS_ADDRESS")
	if address == "" {
		address = "localhost:6379"
	}
	client := redis.NewClient(&redis.Options{Addr: address})
	defer client.Close()
	ctx := context.Background()
	if err := client.Ping(ctx).Err(); err != nil {
		t.Fatal(err)
	}
	store := NewSnapshotStore(client, time.Minute)
	snapshot := Snapshot{Version: SnapshotVersionV1, MappingVersion: "integration-v1", Partition: 11, Offset: 42, Epoch: 3, Occupied: []string{"trip:seat:1", "trip:seat:2"}}
	if err := store.Save(ctx, snapshot); err != nil {
		t.Fatal(err)
	}
	loaded, err := store.Load(ctx, snapshot.MappingVersion, snapshot.Partition)
	if err != nil {
		t.Fatal(err)
	}
	if loaded == nil || loaded.Offset != snapshot.Offset || loaded.Epoch != snapshot.Epoch || len(loaded.Occupied) != 2 {
		t.Fatalf("loaded snapshot = %#v", loaded)
	}
}
