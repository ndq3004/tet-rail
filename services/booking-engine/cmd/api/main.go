package main

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/redis/go-redis/v9"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/commands"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/shards"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/transport"
)

func main() {
	address := os.Getenv("HTTP_ADDRESS")
	if address == "" {
		address = ":8080"
	}

	var handler http.Handler = transport.NewHandler()

	if databaseURL := os.Getenv("DATABASE_URL"); databaseURL != "" {
		pool, err := pgxpool.New(context.Background(), databaseURL)
		if err != nil {
			slog.Error("booking database connection failed", "error", err)
			os.Exit(1)
		}
		defer pool.Close()
		if err := holds.RunMigrations(context.Background(), pool); err != nil {
			slog.Error("booking migration failed", "error", err)
			os.Exit(1)
		}
		brokers := strings.Split(defaultValue(os.Getenv("KAFKA_BROKERS"), "localhost:9092"), ",")
		publisher := events.NewKafkaSeatStatePublisher(brokers)
		defer publisher.Close()
		commandPublisher := commands.NewKafkaPublisher(brokers)
		defer commandPublisher.Close()
		var snapshots *shards.SnapshotStore
		if redisURL := os.Getenv("REDIS_URL"); redisURL != "" {
			options, err := redis.ParseURL(redisURL)
			if err != nil {
				slog.Error("invalid booking Redis configuration")
				os.Exit(1)
			}
			client := redis.NewClient(options)
			defer client.Close()
			snapshots = shards.NewSnapshotStore(client, 24*time.Hour)
		}
		repository := holds.NewRepository(pool)
		commandConsumer, err := commands.NewConsumer(brokers, commands.NewWorker(repository, commands.NewStore(pool)), snapshots)
		if err != nil {
			slog.Error("invalid booking Kafka consumer configuration")
			os.Exit(1)
		}
		handler = transport.NewDevelopmentHandler(os.Getenv("ENABLE_KAFKA_DIAGNOSTICS") == "true", commands.NewService(commands.NewStore(pool), commandPublisher, repository))
		defer commandConsumer.Close()
		go commands.RunPaymentConsumer(context.Background(), brokers, repository)
		expiry := holds.NewExpiryWorker(repository)
		go expiry.Run(context.Background(), time.Second, 100)
		mux := http.NewServeMux()
		mux.Handle("GET /metrics", expiry)
		pressure := &bookingPressure{}
		go pressure.run(context.Background(), pool, commandPublisher, brokers)
		mux.Handle("GET /internal/v1/traffic-pressure", pressure)
		mux.Handle("/", handler)
		handler = mux
		go func() {
			if err := commandConsumer.Run(context.Background()); err != nil {
				slog.Error("hold command worker stopped", "error", err)
			}
		}()
		go holds.NewOutboxDispatcher(repository, publisher).Run(context.Background(), 250*time.Millisecond)
	}
	server := &http.Server{Addr: address, Handler: handler}
	slog.Info("booking engine listening", "address", address)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		slog.Error("booking engine stopped", "error", err)
		os.Exit(1)
	}
}

func defaultValue(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}
