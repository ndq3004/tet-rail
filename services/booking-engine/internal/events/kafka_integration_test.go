//go:build integration

package events

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/segmentio/kafka-go"
)

func TestKafkaPublisherWritesSeatStateV1(t *testing.T) {
	brokers := strings.Split(envOr("KAFKA_BROKERS", "localhost:9092"), ",")
	context, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	partitions := make([]int, 12)
	for index := range partitions {
		partitions[index] = index
	}
	partition := (&kafka.Hash{}).Balance(kafka.Message{Key: []byte("40000000-0000-0000-0000-000000000001:70000000-0000-0000-0000-000000000001")}, partitions...)
	reader := kafka.NewReader(kafka.ReaderConfig{Brokers: brokers, Topic: SeatStateTopicV1, Partition: partition, StartOffset: kafka.FirstOffset})
	defer reader.Close()
	publisher := NewKafkaSeatStatePublisher(brokers)
	defer publisher.Close()
	event := SeatStateEventV1{EventID: "90000000-0000-0000-0000-000000000099", EventType: SeatHeldV1, Version: 1, OccurredAt: time.Now().UTC(), CorrelationID: "90000000-0000-0000-0000-000000000002", CausationID: "90000000-0000-0000-0000-000000000003", Payload: SeatStatePayload{TripID: "40000000-0000-0000-0000-000000000001", SeatID: "70000000-0000-0000-0000-000000000001", SegmentIndices: []int{1, 2}}}
	if err := publisher.Publish(context, event); err != nil {
		t.Fatal(err)
	}
	for {
		message, err := reader.ReadMessage(context)
		if err != nil {
			t.Fatal(err)
		}
		if strings.Contains(string(message.Value), event.EventID) {
			if string(message.Key) != event.Payload.TripID+":"+event.Payload.SeatID {
				t.Fatalf("key=%q", message.Key)
			}
			return
		}
	}
}
func envOr(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}
