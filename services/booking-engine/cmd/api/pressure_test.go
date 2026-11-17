package main

import "testing"

func TestPartitionSkewUsesRecentGrowth(t *testing.T) {
	previous := map[int]int64{0: 1000, 1: 20, 2: 40}
	current := map[int]int64{0: 1002, 1: 35, 2: 42}
	if got := partitionSkew(previous, current); got != 13 {
		t.Fatalf("partition skew = %d, want 13", got)
	}
	if got := partitionSkew(nil, current); got != 0 {
		t.Fatalf("partition skew without baseline = %d", got)
	}
}
