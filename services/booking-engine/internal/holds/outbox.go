package holds

import (
	"context"
	"time"

	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
)

type OutboxDispatcher struct {
	repository *Repository
	publisher  events.SeatStatePublisher
}

func NewOutboxDispatcher(repository *Repository, publisher events.SeatStatePublisher) *OutboxDispatcher {
	return &OutboxDispatcher{repository: repository, publisher: publisher}
}

// DispatchOne publishes a committed outbox record. It deliberately leaves a failed record
// unpublished for retry; consumers deduplicate with event_id if Kafka accepted an earlier attempt.
func (d *OutboxDispatcher) DispatchOne(ctx context.Context) (bool, error) {
	event, err := d.repository.ClaimOutbox(ctx, 30*time.Second)
	if err != nil || event == nil {
		return false, err
	}
	if err = d.publisher.Publish(ctx, event.Payload); err != nil {
		return true, d.repository.ReleaseOutbox(ctx, event.ID, time.Second)
	}
	return true, d.repository.MarkPublished(ctx, event.ID)
}
func (d *OutboxDispatcher) Run(ctx context.Context, idle time.Duration) error {
	for {
		didWork, err := d.DispatchOne(ctx)
		if err != nil {
			return err
		}
		if !didWork {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(idle):
			}
		}
	}
}
