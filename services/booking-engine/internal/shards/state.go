package shards

import (
	"encoding/json"
	"fmt"
	"sync"
)

// State is the recoverable in-memory guard before the PostgreSQL constraint.
type State struct {
	mu       sync.Mutex
	epoch    int64
	occupied map[string]struct{}
}

func New() *State { return &State{occupied: map[string]struct{}{}} }
func (s *State) Claim(epoch int64, keys []string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if epoch != s.epoch {
		return fmt.Errorf("stale shard owner")
	}
	for _, key := range keys {
		if _, ok := s.occupied[key]; ok {
			return fmt.Errorf("occupied")
		}
	}
	for _, key := range keys {
		s.occupied[key] = struct{}{}
	}
	return nil
}
func (s *State) Release(epoch int64, keys []string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if epoch != s.epoch {
		return fmt.Errorf("stale shard owner")
	}
	for _, key := range keys {
		delete(s.occupied, key)
	}
	return nil
}
func (s *State) AdvanceEpoch(epoch int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if epoch > s.epoch {
		s.epoch = epoch
	}
}
func (s *State) Snapshot() ([]byte, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	keys := make([]string, 0, len(s.occupied))
	for k := range s.occupied {
		keys = append(keys, k)
	}
	return json.Marshal(struct {
		Epoch int64    `json:"epoch"`
		Keys  []string `json:"keys"`
	}{s.epoch, keys})
}
func Restore(body []byte) (*State, error) {
	var snap struct {
		Epoch int64    `json:"epoch"`
		Keys  []string `json:"keys"`
	}
	if err := json.Unmarshal(body, &snap); err != nil {
		return nil, err
	}
	s := New()
	s.epoch = snap.Epoch
	for _, k := range snap.Keys {
		s.occupied[k] = struct{}{}
	}
	return s, nil
}
