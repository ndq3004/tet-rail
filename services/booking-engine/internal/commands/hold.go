package commands

import (
	"context"
	"encoding/json"
	"fmt"
	"hash/fnv"
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
	RequestID, OwnerID, IdempotencyKey, CorrelationID string
	ExpiresAt                                         time.Time
	Segments                                          []holds.Segment
}

func (c HoldRequestedV1) ShardKey() (string, error) {
	if len(c.Segments) == 0 {
		return "", fmt.Errorf("at least one segment is required")
	}
	trip := c.Segments[0].TripID
	key := trip + ":" + c.Segments[0].CarriageID
	for _, segment := range c.Segments {
		if segment.TripID != trip || segment.CarriageID == "" || trip+":"+segment.CarriageID != key {
			return "", fmt.Errorf("cross-shard hold commands require saga compensation")
		}
	}
	return key, nil
}

// Shard is the documented v1 mapping. Kafka receives the same stable key and its
// partitioning must be configured with the matching twelve partitions.
func (c HoldRequestedV1) Shard() (int, error) {
	key, err := c.ShardKey()
	if err != nil {
		return 0, err
	}
	h := fnv.New32a()
	_, _ = h.Write([]byte(key))
	return int(h.Sum32() % MappingPartitionsV1), nil
}

type Publisher interface {
	Publish(context.Context, HoldRequestedV1) error
}
type KafkaPublisher struct{ writer *kafka.Writer }

func NewKafkaPublisher(brokers []string) *KafkaPublisher {
	return &KafkaPublisher{writer: &kafka.Writer{Addr: kafka.TCP(brokers...), Topic: TopicV1, RequiredAcks: kafka.RequireAll, Async: false, Balancer: &kafka.Hash{}}}
}
func (p *KafkaPublisher) Publish(ctx context.Context, command HoldRequestedV1) error {
	key, err := command.ShardKey()
	if err != nil {
		return err
	}
	body, err := json.Marshal(command)
	if err != nil {
		return err
	}
	return p.writer.WriteMessages(ctx, kafka.Message{Key: []byte(key), Value: body})
}
func (p *KafkaPublisher) Close() error { return p.writer.Close() }

type Handler interface {
	Handle(context.Context, HoldRequestedV1) error
}
type Consumer struct {
	reader  *kafka.Reader
	handler Handler
}

func NewConsumer(brokers []string, handler Handler) *Consumer {
	return &Consumer{reader: kafka.NewReader(kafka.ReaderConfig{Brokers: brokers, Topic: TopicV1, GroupID: "booking-hold-worker-v1", MinBytes: 1, MaxBytes: 10e6}), handler: handler}
}
func (c *Consumer) Run(ctx context.Context) error {
	for {
		message, err := c.reader.FetchMessage(ctx)
		if err != nil {
			return err
		}
		var command HoldRequestedV1
		if err = json.Unmarshal(message.Value, &command); err == nil {
			err = c.handler.Handle(ctx, command)
		}
		if err != nil {
			return err
		}
		if err = c.reader.CommitMessages(ctx, message); err != nil {
			return err
		}
	}
}
func (c *Consumer) Close() error { return c.reader.Close() }
