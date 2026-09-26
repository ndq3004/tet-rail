package shards

import "testing"

func TestSnapshotValidationRejectsStaleOrAmbiguousMetadata(t *testing.T) {
	tests := []Snapshot{
		{Version: 0, MappingVersion: "v1", Partition: 0, Offset: 0, Epoch: 0},
		{Version: SnapshotVersionV1, MappingVersion: "", Partition: 0, Offset: 0, Epoch: 0},
		{Version: SnapshotVersionV1, MappingVersion: "v1", Partition: -1, Offset: 0, Epoch: 0},
		{Version: SnapshotVersionV1, MappingVersion: "v1", Partition: 0, Offset: 0, Epoch: 0, Occupied: []string{"seat:1", "seat:1"}},
	}
	for _, snapshot := range tests {
		if err := snapshot.validate(); err == nil {
			t.Fatalf("snapshot %#v was accepted", snapshot)
		}
	}
}
