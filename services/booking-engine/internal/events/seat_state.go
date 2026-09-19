package events

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/segmentio/kafka-go"
)

const SeatStateTopicV1 = "booking.seat-state.v1"

const (
	SeatHeldV1      = "SeatHeld.v1"
	HoldExpiredV1   = "HoldExpired.v1"
	HoldConfirmedV1 = "HoldConfirmed.v1"
)

// SeatStateEventV1 is the versioned Kafka value. Kafka metadata supplies source_position;
// producers must not manufacture an ordering number in this payload.
type SeatStateEventV1 struct {
	EventID       string           `json:"event_id"`
	EventType     string           `json:"event_type"`
	Version       int              `json:"version"`
	OccurredAt    time.Time        `json:"occurred_at"`
	CorrelationID string           `json:"correlation_id"`
	CausationID   string           `json:"causation_id"`
	Payload       SeatStatePayload `json:"payload"`
}

type SeatStatePayload struct {
	TripID         string `json:"trip_id"`
	SeatID         string `json:"seat_id"`
	SegmentIndices []int  `json:"segment_indices"`
}

type SeatStatePublisher interface {
	Publish(ctx context.Context, event SeatStateEventV1) error
}

type KafkaSeatStatePublisher struct {
	writer *kafka.Writer
}

func NewKafkaSeatStatePublisher(brokers []string) *KafkaSeatStatePublisher {
	return &KafkaSeatStatePublisher{writer: &kafka.Writer{
		Addr:         kafka.TCP(brokers...),
		Topic:        SeatStateTopicV1,
		RequiredAcks: kafka.RequireAll,
		Async:        false,
		Balancer:     &kafka.Hash{},
	}}
}

func (p *KafkaSeatStatePublisher) Publish(ctx context.Context, event SeatStateEventV1) error {
	if err := validate(event); err != nil {
		return err
	}
	value, err := json.Marshal(event)
	if err != nil {
		return fmt.Errorf("marshal seat state event: %w", err)
	}
	return p.writer.WriteMessages(ctx, kafka.Message{
		Key:   []byte(event.Payload.TripID + ":" + event.Payload.SeatID),
		Value: value,
	})
}

func (p *KafkaSeatStatePublisher) Close() error { return p.writer.Close() }

func validate(event SeatStateEventV1) error {
	if event.EventID == "" || event.CorrelationID == "" || event.CausationID == "" || event.Payload.TripID == "" || event.Payload.SeatID == "" {
		return fmt.Errorf("seat state event identifiers are required")
	}
	if event.Version != 1 || (event.EventType != SeatHeldV1 && event.EventType != HoldExpiredV1 && event.EventType != HoldConfirmedV1) {
		return fmt.Errorf("unsupported seat state event type or version")
	}
	if len(event.Payload.SegmentIndices) == 0 {
		return fmt.Errorf("at least one segment is required")
	}
	seen := make(map[int]struct{}, len(event.Payload.SegmentIndices))
	for _, segment := range event.Payload.SegmentIndices {
		if segment < 1 {
			return fmt.Errorf("segment index must be positive")
		}
		if _, duplicate := seen[segment]; duplicate {
			return fmt.Errorf("segment indices must be unique")
		}
		seen[segment] = struct{}{}
	}
	return nil
}
