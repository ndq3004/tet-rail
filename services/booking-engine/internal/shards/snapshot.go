package shards

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	"github.com/redis/go-redis/v9"
)

const SnapshotVersionV1 = 1
const snapshotKeyPrefix = "booking:shard-snapshot:v1:"

// Snapshot is an internal, versioned recovery record. It is only a fast recovery
// aid: Kafka replay and PostgreSQL remain authoritative when it is absent or stale.
type Snapshot struct {
	Version        int      `json:"version"`
	MappingVersion string   `json:"mapping_version"`
	Partition      int      `json:"partition"`
	Offset         int64    `json:"offset"`
	Epoch          int64    `json:"epoch"`
	Occupied       []string `json:"occupied"`
}

type SnapshotStore struct {
	client redis.UniversalClient
	ttl    time.Duration
}

func NewSnapshotStore(client redis.UniversalClient, ttl time.Duration) *SnapshotStore {
	return &SnapshotStore{client: client, ttl: ttl}
}

func (s *SnapshotStore) Save(ctx context.Context, snapshot Snapshot) error {
	if err := snapshot.validate(); err != nil {
		return err
	}
	sort.Strings(snapshot.Occupied)
	body, err := json.Marshal(snapshot)
	if err != nil {
		return err
	}
	return s.client.Set(ctx, snapshotKey(snapshot.MappingVersion, snapshot.Partition), body, s.ttl).Err()
}

func (s *SnapshotStore) Load(ctx context.Context, mappingVersion string, partition int) (*Snapshot, error) {
	body, err := s.client.Get(ctx, snapshotKey(mappingVersion, partition)).Bytes()
	if err == redis.Nil {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var snapshot Snapshot
	if err := json.Unmarshal(body, &snapshot); err != nil {
		return nil, fmt.Errorf("decode shard snapshot: %w", err)
	}
	if err := snapshot.validate(); err != nil {
		return nil, fmt.Errorf("invalid shard snapshot: %w", err)
	}
	if snapshot.MappingVersion != mappingVersion || snapshot.Partition != partition {
		return nil, fmt.Errorf("snapshot identity does not match requested shard")
	}
	return &snapshot, nil
}

func snapshotKey(mappingVersion string, partition int) string {
	return fmt.Sprintf("%s%s:%d", snapshotKeyPrefix, mappingVersion, partition)
}

func (s Snapshot) validate() error {
	if s.Version != SnapshotVersionV1 || s.MappingVersion == "" || s.Partition < 0 || s.Offset < 0 || s.Epoch < 0 {
		return fmt.Errorf("invalid snapshot metadata")
	}
	seen := make(map[string]struct{}, len(s.Occupied))
	for _, key := range s.Occupied {
		if key == "" {
			return fmt.Errorf("empty occupied key")
		}
		if _, duplicate := seen[key]; duplicate {
			return fmt.Errorf("duplicate occupied key")
		}
		seen[key] = struct{}{}
	}
	return nil
}
