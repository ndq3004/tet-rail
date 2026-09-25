package shards

import "testing"

func TestSnapshotRestoreAndEpochFence(t *testing.T) {
	s := New()
	s.AdvanceEpoch(2)
	if err := s.Claim(2, []string{"seat:1"}); err != nil {
		t.Fatal(err)
	}
	body, err := s.Snapshot()
	if err != nil {
		t.Fatal(err)
	}
	restored, err := Restore(body)
	if err != nil {
		t.Fatal(err)
	}
	if err := restored.Claim(1, []string{"seat:2"}); err == nil {
		t.Fatal("stale owner was accepted")
	}
	if err := restored.Claim(2, []string{"seat:1"}); err == nil {
		t.Fatal("restored seat was available")
	}
}
