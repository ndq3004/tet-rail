package events

import (
	"testing"
	"time"
)

func TestValidateAcceptsV1Event(t *testing.T) {
	event := validEvent()
	if err := validate(event); err != nil {
		t.Fatalf("validate() error = %v", err)
	}
}

func TestValidateRejectsDuplicateSegment(t *testing.T) {
	event := validEvent()
	event.Payload.SegmentIndices = []int{1, 1}
	if err := validate(event); err == nil {
		t.Fatal("validate() error = nil, want duplicate segment error")
	}
}

func validEvent() SeatStateEventV1 {
	return SeatStateEventV1{EventID: "event", EventType: SeatHeldV1, Version: 1, OccurredAt: time.Now().UTC(), CorrelationID: "correlation", CausationID: "cause", Payload: SeatStatePayload{TripID: "trip", SeatID: "seat", SegmentIndices: []int{1, 2}}}
}
