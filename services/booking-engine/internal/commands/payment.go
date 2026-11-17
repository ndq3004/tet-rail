package commands

import (
	"context"
	"encoding/json"
	"log/slog"
	"time"

	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

func RunPaymentConsumer(ctx context.Context, brokers []string, repository *holds.Repository) {
	reader := kafka.NewReader(kafka.ReaderConfig{
		Brokers: brokers, GroupID: "booking-payment-confirmation-v1",
		Topic: holds.PaymentTopicV1, StartOffset: kafka.FirstOffset,
	})
	defer reader.Close()
	writer := &kafka.Writer{
		Addr: kafka.TCP(brokers...), Topic: holds.PaymentTopicV1,
		Balancer: &kafka.Hash{}, RequiredAcks: kafka.RequireAll,
		BatchTimeout: 10 * time.Millisecond, WriteTimeout: 5 * time.Second,
	}
	defer writer.Close()

	for ctx.Err() == nil {
		message, err := reader.FetchMessage(ctx)
		if err != nil {
			if ctx.Err() == nil {
				slog.Warn("payment authorization fetch will retry")
				time.Sleep(time.Second)
			}
			continue
		}
		var event holds.PaymentEvent
		if err := json.Unmarshal(message.Value, &event); err == nil &&
			event.EventType == "PaymentAuthorized.v1" && event.Version == 1 {
			for ctx.Err() == nil {
				result, err := repository.ConfirmPayment(ctx, event)
				if err == nil {
					err = writer.WriteMessages(ctx, kafka.Message{Key: []byte(event.Payload.OrderID), Value: result})
				}
				if err == nil {
					break
				}
				slog.Warn("payment confirmation will retry")
				time.Sleep(time.Second)
			}
		}
		// Retry the same offset before fetching the next record.
		for ctx.Err() == nil {
			if err := reader.CommitMessages(ctx, message); err == nil {
				break
			}
			time.Sleep(time.Second)
		}
	}
}
