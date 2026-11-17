package commands

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/shards"
)

type Consumer struct {
	brokers   []string
	group     *kafka.ConsumerGroup
	worker    *Worker
	snapshots *shards.SnapshotStore
}

func NewConsumer(brokers []string, worker *Worker, snapshots *shards.SnapshotStore) (*Consumer, error) {
	group, err := kafka.NewConsumerGroup(kafka.ConsumerGroupConfig{
		Brokers: brokers, ID: "booking-hold-worker-v1", Topics: []string{TopicV1}, StartOffset: kafka.FirstOffset,
	})
	if err != nil {
		return nil, err
	}
	return &Consumer{brokers: brokers, group: group, worker: worker, snapshots: snapshots}, nil
}

func (c *Consumer) Run(ctx context.Context) error {
	for {
		generation, err := c.group.Next(ctx)
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, kafka.ErrGroupClosed) {
				return err
			}
			slog.Warn("booking consumer group join failed", "error", err)
			continue
		}
		for _, assignment := range generation.Assignments[TopicV1] {
			generation.Start(func(assignmentCtx context.Context) {
				if err := c.consume(assignmentCtx, generation, assignment); err != nil && assignmentCtx.Err() == nil {
					slog.Warn("booking partition worker interrupted", "partition", assignment.ID, "error_type", fmt.Sprintf("%T", err))
					select {
					case <-assignmentCtx.Done():
					case <-time.After(time.Second):
					}
				}
			})
		}
	}
}

func (c *Consumer) Close() error { return c.group.Close() }

func (c *Consumer) consume(ctx context.Context, generation *kafka.Generation, assignment kafka.PartitionAssignment) error {
	epoch, err := c.worker.acquireEpoch(ctx, assignment.ID)
	if err != nil {
		return err
	}
	reader := kafka.NewReader(kafka.ReaderConfig{
		Brokers: c.brokers, Topic: TopicV1, Partition: assignment.ID, MinBytes: 1, MaxBytes: 10e6, OffsetOutOfRangeError: true,
	})
	defer reader.Close()

	start := int64(kafka.FirstOffset)
	occupied := make(map[string]struct{})
	if c.snapshots != nil {
		snapshot, err := c.snapshots.Load(ctx, MappingVersionV1, assignment.ID)
		switch {
		case err != nil:
			slog.Warn("booking snapshot unavailable", "partition", assignment.ID)
		case snapshot != nil && snapshot.Epoch < epoch && snapshot.Offset < assignment.Offset:
			start = snapshot.Offset + 1
			for _, key := range snapshot.Occupied {
				occupied[key] = struct{}{}
			}
			slog.Info("booking shard snapshot restored", "partition", assignment.ID, "offset", snapshot.Offset, "replay_from", start)
		}
	}
	if err := reader.SetOffset(start); err != nil {
		return err
	}

	var replayed int
	recoveryStarted := time.Now()
	recovered := false
	for {
		message, err := reader.FetchMessage(ctx)
		if err != nil {
			if errors.Is(err, kafka.OffsetOutOfRange) && !recovered && start >= 0 {
				clear(occupied)
				start = kafka.FirstOffset
				if err := reader.SetOffset(start); err != nil {
					return err
				}
				continue
			}
			return err
		}
		if !recovered && message.Offset >= assignment.Offset {
			slog.Info("booking partition replay completed", "partition", assignment.ID, "replayed", replayed, "duration", time.Since(recoveryStarted))
			recovered = true
		}
		var command HoldRequestedV1
		if err := json.Unmarshal(message.Value, &command); err != nil {
			return err
		}
		if err := c.worker.handle(ctx, command, assignment.ID, epoch); err != nil {
			if errors.Is(err, errStaleOwner) {
				slog.Warn("booking shard owner fenced", "partition", assignment.ID)
			}
			return err
		}
		for _, segment := range command.Segments {
			if c.snapshots == nil {
				break
			}
			if shardForKey([]byte(segment.TripID+":"+segment.CarriageID)) != assignment.ID {
				continue
			}
			key := fmt.Sprintf("%s:%s:%d", segment.TripID, segment.SeatID, segment.Index)
			busy, err := c.worker.holds.Occupied(ctx, segment)
			if err != nil {
				return err
			}
			if busy {
				occupied[key] = struct{}{}
			} else {
				delete(occupied, key)
			}
		}
		if !recovered {
			replayed++
			continue
		}
		if err := generation.CommitOffsets(map[string]map[int]int64{TopicV1: {assignment.ID: message.Offset + 1}}); err != nil {
			return err
		}
		if c.snapshots != nil {
			keys := make([]string, 0, len(occupied))
			for key := range occupied {
				keys = append(keys, key)
			}
			err := c.snapshots.Save(ctx, shards.Snapshot{
				Version: shards.SnapshotVersionV1, MappingVersion: MappingVersionV1,
				Partition: assignment.ID, Offset: message.Offset, Epoch: epoch, Occupied: keys,
			})
			if err != nil {
				slog.Warn("booking snapshot write failed", "partition", assignment.ID)
			}
		}
	}
}
