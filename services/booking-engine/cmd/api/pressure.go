package main

import (
	"context"
	"encoding/json"
	"net/http"
	"sync/atomic"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/commands"
)

type pressureSample struct {
	SampledAt          time.Time `json:"sampledAt"`
	DatabasePercent    int       `json:"databasePercent"`
	Queued             int       `json:"queued"`
	OldestMS           int64     `json:"oldestMs"`
	LogicalActiveHolds int       `json:"logicalActiveHolds"`
	ActiveHolds        int       `json:"activeHolds"`
	KafkaWriteMS       int64     `json:"kafkaWriteMs"`
	KafkaErrors        int64     `json:"kafkaErrors"`
	KafkaSkew          int64     `json:"kafkaSkew"`
}

type bookingPressure struct {
	latest atomic.Pointer[pressureSample]
}

func (p *bookingPressure) run(ctx context.Context, pool *pgxpool.Pool, publisher *commands.KafkaPublisher, brokers []string) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	broker := &kafka.Client{Addr: kafka.TCP(brokers...)}
	var previous map[int]int64
	for {
		sample := pressureSample{SampledAt: time.Now().UTC()}
		connections := pool.Stat()
		sample.DatabasePercent = int(100 * connections.AcquiredConns() / connections.MaxConns())
		attempt, cancel := context.WithTimeout(ctx, 750*time.Millisecond)
		err := pool.QueryRow(attempt, `
			WITH pending AS (SELECT created_at FROM booking.hold_requests WHERE status='QUEUED' AND acknowledged_at IS NOT NULL
                AND (command_envelope->>'expires_at')::timestamptz > now() ORDER BY created_at LIMIT 10001)
			SELECT count(*),coalesce(greatest(0,extract(epoch from now()-min(created_at))*1000)::bigint,0),
			       (SELECT count(*) FROM (SELECT 1 FROM booking.holds WHERE state='ACTIVE' AND expires_at>now() LIMIT 10001) active),
                (SELECT count(*) FROM (
                    (SELECT 1 FROM booking.hold_sagas WHERE state='HELD' AND expires_at>now() LIMIT 10001)
                    UNION ALL
                    (SELECT 1 FROM booking.holds h WHERE state='ACTIVE' AND expires_at>now()
                     AND NOT EXISTS (SELECT 1 FROM booking.hold_saga_steps WHERE hold_id=h.id) LIMIT 10001)
                    LIMIT 10001
                ) logical)
			FROM pending`).Scan(&sample.Queued, &sample.OldestMS, &sample.ActiveHolds, &sample.LogicalActiveHolds)
		if err == nil {
			metadata, err := broker.Metadata(attempt, &kafka.MetadataRequest{Topics: []string{commands.TopicV1}})
			if err != nil || len(metadata.Topics) != 1 || metadata.Topics[0].Error != nil {
				sample.KafkaErrors++
			} else {
				partitions := metadata.Topics[0].Partitions
				offsets := make([]kafka.OffsetRequest, 0, len(partitions))
				for _, partition := range partitions {
					offsets = append(offsets, kafka.LastOffsetOf(partition.ID))
				}
				response, err := broker.ListOffsets(attempt, &kafka.ListOffsetsRequest{
					Topics: map[string][]kafka.OffsetRequest{commands.TopicV1: offsets},
				})
				if err != nil {
					sample.KafkaErrors++
				} else {
					current := make(map[int]int64, len(offsets))
					for _, partition := range response.Topics[commands.TopicV1] {
						if partition.Error != nil || partition.LastOffset < 0 {
							sample.KafkaErrors++
							continue
						}
						current[partition.Partition] = partition.LastOffset
					}
					if len(current) != commands.MappingPartitionsV1 {
						sample.KafkaErrors++
					} else {
						sample.KafkaSkew = partitionSkew(previous, current)
						previous = current
					}
				}
			}
			stats := publisher.Stats()
			sample.KafkaErrors += stats.Errors
			sample.KafkaWriteMS = stats.WriteTime.Max.Milliseconds()
			p.latest.Store(&sample)
		} else {
			p.latest.Store(nil)
		}
		cancel()
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (p *bookingPressure) ServeHTTP(w http.ResponseWriter, _ *http.Request) {
	sample := p.latest.Load()
	if sample == nil || time.Since(sample.SampledAt) > 3*time.Second {
		http.Error(w, "pressure sample unavailable", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(sample)
}

func partitionSkew(previous, current map[int]int64) int64 {
	if len(previous) != len(current) {
		return 0
	}
	var low, high int64
	first := true
	for partition, offset := range current {
		baseline, ok := previous[partition]
		if !ok || offset < baseline {
			return 0
		}
		delta := offset - baseline
		if first || delta < low {
			low = delta
		}
		high = max(high, delta)
		first = false
	}
	return high - low
}
