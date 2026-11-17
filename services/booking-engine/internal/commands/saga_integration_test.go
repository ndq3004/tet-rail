//go:build integration

package commands

import (
	"context"
	"errors"
	"os"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/redis/go-redis/v9"
	"github.com/segmentio/kafka-go"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/shards"
)

func TestCrossCarriageSagaCreatesLogicalHoldAndCompensatesConflict(t *testing.T) {
	ctx := context.Background()
	url := os.Getenv("BOOKING_TEST_DATABASE_URL")
	if url == "" {
		url = "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail?search_path=booking"
	}
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if err := holds.RunMigrations(ctx, pool); err != nil {
		t.Fatal(err)
	}

	repository := holds.NewRepository(pool)
	store := NewStore(pool)
	worker := NewWorker(repository, store)
	now := time.Now().UTC()
	tripID := holds.NewID()
	command := HoldRequestedV1{
		RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(), CorrelationID: holds.NewID(), ExpiresAt: now.Add(time.Minute),
		Segments: []holds.Segment{{TripID: tripID, CarriageID: "A", SeatID: holds.NewID(), Index: 1}, {TripID: tripID, CarriageID: "B", SeatID: holds.NewID(), Index: 1}},
	}
	if _, created, err := store.Begin(ctx, command); err != nil || !created {
		t.Fatalf("Begin() = created:%v err:%v", created, err)
	}
	if err := worker.Handle(ctx, command); err != nil {
		t.Fatal(err)
	}
	result, err := store.Get(ctx, command.RequestID)
	if err != nil || result.Status != "HELD" || result.HoldID == "" || result.ExpiresAt == nil {
		t.Fatalf("result = %#v, err = %v", result, err)
	}
	logicalHold, err := repository.Get(ctx, result.HoldID)
	if err != nil || logicalHold.State != "HELD" || logicalHold.OwnerID != command.OwnerID {
		t.Fatalf("logical hold = %#v, err = %v", logicalHold, err)
	}
	if err := worker.Handle(ctx, command); err != nil {
		t.Fatalf("duplicate worker delivery returned %v", err)
	}

	compensatedSeat := holds.NewID()
	conflicting := HoldRequestedV1{
		RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(), CorrelationID: holds.NewID(), ExpiresAt: now.Add(time.Minute),
		Segments: []holds.Segment{{TripID: tripID, CarriageID: "A", SeatID: compensatedSeat, Index: 1}, {TripID: tripID, CarriageID: "B", SeatID: command.Segments[1].SeatID, Index: 1}},
	}
	if _, created, err := store.Begin(ctx, conflicting); err != nil || !created {
		t.Fatalf("Begin(conflicting) = created:%v err:%v", created, err)
	}
	if err := worker.Handle(ctx, conflicting); err != nil {
		t.Fatal(err)
	}
	result, err = store.Get(ctx, conflicting.RequestID)
	if err != nil || result.Status != "REJECTED" {
		t.Fatalf("conflicting result = %#v, err = %v", result, err)
	}
	var held int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM booking.held_segments WHERE trip_id=$1 AND seat_id=$2`, tripID, compensatedSeat).Scan(&held); err != nil {
		t.Fatal(err)
	}
	if held != 0 {
		t.Fatalf("compensation left %d held segments", held)
	}
}

type publisherFunc func(context.Context, HoldRequestedV1) error

func (publish publisherFunc) Publish(ctx context.Context, command HoldRequestedV1) error {
	return publish(ctx, command)
}

func TestRetryPreservesCommandAndWaitsForAcknowledgement(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	store := NewStore(pool)
	submissions := make(chan HoldRequestedV1, 2)
	release := make(chan struct{})
	publishError := errors.New("ambiguous Kafka acknowledgement")
	service := NewService(store, publisherFunc(func(ctx context.Context, command HoldRequestedV1) error {
		submissions <- command
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-release:
			return publishError
		}
	}))
	owner, key := holds.NewID(), holds.NewID()
	segments := []holds.Segment{{TripID: holds.NewID(), CarriageID: "A", SeatID: holds.NewID(), Index: 1}}
	first := make(chan error, 1)
	go func() {
		_, err := service.Submit(ctx, owner, key, holds.NewID(), time.Now().Add(time.Minute), segments)
		first <- err
	}()
	var original HoldRequestedV1
	select {
	case original = <-submissions:
	case err := <-first:
		t.Fatalf("first submission failed before publication: %v", err)
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	retry := NewService(store, publisherFunc(func(_ context.Context, command HoldRequestedV1) error {
		submissions <- command
		return nil
	}))
	result, err := retry.Submit(ctx, owner, key, holds.NewID(), time.Now().Add(2*time.Minute), segments)
	close(release)
	if err != nil || result.RequestID != original.RequestID {
		t.Fatalf("retry result = %v, %v", result, err)
	}
	select {
	case command := <-submissions:
		if command.RequestID != original.RequestID || command.CorrelationID != original.CorrelationID || !command.ExpiresAt.Equal(original.ExpiresAt) {
			t.Fatal("retry changed the original command")
		}
	default:
		t.Fatal("retry returned before an acknowledged publication")
	}
	if err := <-first; !errors.Is(err, publishError) {
		t.Fatalf("first publish returned %v", err)
	}
	result, err = retry.Submit(ctx, owner, key, holds.NewID(), time.Now().Add(time.Minute), segments)
	if err != nil || result.RequestID != original.RequestID || len(submissions) != 0 {
		t.Fatalf("acknowledged retry = %v, %v", result, err)
	}
	segments[0].Index++
	if _, err := retry.Submit(ctx, owner, key, holds.NewID(), time.Now().Add(time.Minute), segments); !errors.Is(err, ErrIdempotencyConflict) {
		t.Fatalf("changed payload returned %v", err)
	}
}

func TestPreviousPartitionOwnerCannotMutateHold(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	store := NewStore(pool)
	worker := NewWorker(holds.NewRepository(pool), store)
	command := HoldRequestedV1{
		RequestID: holds.NewID(), OwnerID: holds.NewID(), IdempotencyKey: holds.NewID(),
		CorrelationID: holds.NewID(), ExpiresAt: time.Now().Add(time.Minute),
		Segments: []holds.Segment{{TripID: holds.NewID(), CarriageID: "A", SeatID: holds.NewID(), Index: 1}},
	}
	partition, err := command.Shard()
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := store.Begin(ctx, command); err != nil {
		t.Fatal(err)
	}
	previous, err := worker.acquireEpoch(ctx, partition)
	if err != nil {
		t.Fatal(err)
	}
	current, err := worker.acquireEpoch(ctx, partition)
	if err != nil {
		t.Fatal(err)
	}
	if err := worker.handle(ctx, command, partition, previous); !errors.Is(err, errStaleOwner) {
		t.Fatalf("previous owner returned %v", err)
	}
	if err := worker.handle(ctx, command, partition, current); err != nil {
		t.Fatal(err)
	}
	result, err := store.Get(ctx, command.RequestID)
	if err != nil || result.Status != "HELD" {
		t.Fatalf("current owner result = %v, %v", result, err)
	}
}

func integrationPool(t *testing.T, ctx context.Context) *pgxpool.Pool {
	t.Helper()
	url := os.Getenv("BOOKING_TEST_DATABASE_URL")
	if url == "" {
		url = "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail?search_path=booking"
	}
	pool, err := pgxpool.New(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if err := holds.RunMigrations(ctx, pool); err != nil {
		t.Fatal(err)
	}
	return pool
}

func TestKafkaConsumerResumesAfterRestart(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	pool := integrationPool(t, ctx)
	store := NewStore(pool)
	worker := NewWorker(holds.NewRepository(pool), store)
	brokerList := os.Getenv("BOOKING_TEST_KAFKA_BROKERS")
	if brokerList == "" {
		brokerList = "localhost:9092"
	}
	brokers := strings.Split(brokerList, ",")
	redisAddress := os.Getenv("BOOKING_TEST_REDIS_ADDRESS")
	if redisAddress == "" {
		redisAddress = "localhost:6379"
	}
	redisClient := redis.NewClient(&redis.Options{Addr: redisAddress, DB: 14})
	defer redisClient.Close()
	snapshots := shards.NewSnapshotStore(redisClient, time.Minute)
	groupID := "booking-restart-test-" + holds.NewID()
	start := func() func() {
		group, err := kafka.NewConsumerGroup(kafka.ConsumerGroupConfig{
			Brokers: brokers, ID: groupID, Topics: []string{TopicV1}, StartOffset: kafka.FirstOffset,
		})
		if err != nil {
			t.Fatal(err)
		}
		consumer := &Consumer{group: group, brokers: brokers, worker: worker, snapshots: snapshots}
		done := make(chan struct{})
		go func() {
			defer close(done)
			_ = consumer.Run(ctx)
		}()
		return func() {
			_ = consumer.Close()
			<-done
		}
	}
	publisher := NewKafkaPublisher(brokers)
	defer publisher.Close()
	service := NewService(store, publisher)
	owner := holds.NewID()
	trip := holds.NewID()
	seat := holds.NewID()
	partition := shardForKey([]byte(trip + ":A"))
	var saved *shards.Snapshot
	submit := func(want string, segments []holds.Segment) {
		result, err := service.Submit(ctx, owner, holds.NewID(), holds.NewID(), time.Now().Add(time.Minute), segments)
		if err != nil {
			t.Fatal(err)
		}
		for {
			result, err := store.Get(ctx, result.RequestID)
			if err != nil {
				t.Fatal(err)
			}
			if result.Status != "QUEUED" {
				if result.Status != want {
					t.Fatalf("result status = %s, want %s", result.Status, want)
				}
				return
			}
			select {
			case <-ctx.Done():
				t.Fatal(ctx.Err())
			case <-time.After(25 * time.Millisecond):
			}
		}
	}
	func() {
		stop := start()
		defer stop()
		submit("HELD", []holds.Segment{{TripID: trip, CarriageID: "A", SeatID: seat, Index: 1}})
		wantedKey := trip + ":" + seat + ":1"
		for {
			snapshot, err := snapshots.Load(ctx, MappingVersionV1, partition)
			if err != nil {
				t.Fatal(err)
			}
			if snapshot != nil && slices.Contains(snapshot.Occupied, wantedKey) {
				saved = snapshot
				break
			}
			select {
			case <-ctx.Done():
				t.Fatal("worker did not persist the occupied segment in Redis")
			case <-time.After(25 * time.Millisecond):
			}
		}
	}()
	// The extra marker can only survive if the replacement worker loads the
	// Redis snapshot. No command in Kafka can reconstruct this key.
	marker := holds.NewID() + ":" + holds.NewID() + ":1"
	saved.Occupied = append(saved.Occupied, marker)
	if err := snapshots.Save(ctx, *saved); err != nil {
		t.Fatal(err)
	}
	stop := start()
	defer stop()
	submit("REJECTED", []holds.Segment{{TripID: trip, CarriageID: "A", SeatID: seat, Index: 1}})
	for {
		snapshot, err := snapshots.Load(ctx, MappingVersionV1, partition)
		if err != nil {
			t.Fatal(err)
		}
		if snapshot != nil && snapshot.Epoch > saved.Epoch && snapshot.Offset > saved.Offset {
			if !slices.Contains(snapshot.Occupied, marker) {
				t.Fatal("replacement worker did not restore the Redis snapshot")
			}
			break
		}
		select {
		case <-ctx.Done():
			t.Fatal("replacement worker did not publish a new snapshot")
		case <-time.After(25 * time.Millisecond):
		}
	}
	stopSecond := start()
	defer stopSecond()
	previous, err := snapshots.Load(ctx, MappingVersionV1, partition)
	if err != nil {
		t.Fatal(err)
	}
	for {
		var epoch int64
		err := pool.QueryRow(ctx, `SELECT epoch FROM booking.shard_epochs
			WHERE mapping_version=$1 AND partition_id=$2`, MappingVersionV1, partition).Scan(&epoch)
		if err != nil {
			t.Fatal(err)
		}
		if epoch > previous.Epoch {
			break
		}
		select {
		case <-ctx.Done():
			t.Fatal("joining worker did not trigger a new partition assignment")
		case <-time.After(25 * time.Millisecond):
		}
	}
	other := holds.NewID()
	submit("REJECTED", []holds.Segment{
		{TripID: trip, CarriageID: "0", SeatID: other, Index: 1},
		{TripID: trip, CarriageID: "A", SeatID: seat, Index: 1},
	})
	busy, err := worker.holds.Occupied(ctx, holds.Segment{TripID: trip, SeatID: other, Index: 1})
	if err != nil || busy {
		t.Fatalf("compensated segment occupied = %v, err = %v", busy, err)
	}
}
