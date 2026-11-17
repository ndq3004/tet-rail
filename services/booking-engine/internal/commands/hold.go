package commands

import (
	"context"
	"encoding/json"
	"fmt"
	"hash/fnv"
	"sort"
	"time"

	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

const TopicV1 = "booking.hold-commands.v1"
const MappingVersionV1 = "v1"
const MappingPartitionsV1 = 12

// HoldRequestedV1 is deliberately separate from seat-state events: it is a durable command,
// not an assertion that inventory was allocated.
type HoldRequestedV1 struct {
	RequestID      string          `json:"request_id"`
	OwnerID        string          `json:"owner_id"`
	IdempotencyKey string          `json:"idempotency_key"`
	CorrelationID  string          `json:"correlation_id"`
	ExpiresAt      time.Time       `json:"expires_at"`
	Segments       []holds.Segment `json:"segments"`
}

func (c HoldRequestedV1) ShardKey() (string, error) {
	if len(c.Segments) == 0 {
		return "", fmt.Errorf("at least one segment is required")
	}
	trip := c.Segments[0].TripID
	key := trip + ":" + c.Segments[0].CarriageID
	for _, segment := range c.Segments {
		if segment.TripID != trip || segment.CarriageID == "" {
			return "", fmt.Errorf("segments require one trip and carriage")
		}
	}
	return key, nil
}

func (c HoldRequestedV1) ShardKeys() ([]string, error) {
	if len(c.Segments) == 0 {
		return nil, fmt.Errorf("at least one segment is required")
	}
	trip := c.Segments[0].TripID
	keys := map[string]struct{}{}
	for _, segment := range c.Segments {
		if segment.TripID != trip || segment.CarriageID == "" {
			return nil, fmt.Errorf("segments require one trip and carriage")
		}
		keys[trip+":"+segment.CarriageID] = struct{}{}
	}
	out := make([]string, 0, len(keys))
	for key := range keys {
		out = append(out, key)
	}
	sort.Strings(out)
	return out, nil
}

// Shard is the documented v1 mapping. Kafka receives the same stable key and its
// partitioning must be configured with the matching twelve partitions.
func (c HoldRequestedV1) Shard() (int, error) {
	key, err := c.ShardKey()
	if err != nil {
		return 0, err
	}
	return shardForKey([]byte(key)), nil
}

type Publisher interface {
	Publish(context.Context, HoldRequestedV1) error
}
type KafkaPublisher struct{ writer *kafka.Writer }

func NewKafkaPublisher(brokers []string) *KafkaPublisher {
	return &KafkaPublisher{writer: &kafka.Writer{Addr: kafka.TCP(brokers...), Topic: TopicV1, RequiredAcks: kafka.RequireAll, Async: false, Balancer: shardBalancer{}}}
}
func (p *KafkaPublisher) Publish(ctx context.Context, command HoldRequestedV1) error {
	keys, err := command.ShardKeys()
	if err != nil {
		return err
	}
	body, err := json.Marshal(command)
	if err != nil {
		return err
	}
	// A multi-carriage command is coordinated by the deterministic first shard.
	// Its worker owns the saga and records compensation durably before reporting a result.
	return p.writer.WriteMessages(ctx, kafka.Message{Key: []byte(keys[0]), Value: body})
}
func (p *KafkaPublisher) Stats() kafka.WriterStats { return p.writer.Stats() }
func (p *KafkaPublisher) Close() error             { return p.writer.Close() }

func shardForKey(key []byte) int {
	hash := fnv.New32a()
	_, _ = hash.Write(key)
	return int(hash.Sum32() % MappingPartitionsV1)
}

type shardBalancer struct{}

func (shardBalancer) Balance(message kafka.Message, _ ...int) int {
	return shardForKey(message.Key)
}
