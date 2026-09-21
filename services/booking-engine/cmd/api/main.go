package main

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/commands"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/events"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
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
		commandConsumer := commands.NewConsumer(brokers, commands.NewWorker(holds.NewRepository(pool), commands.NewStore(pool)))
		handler = transport.NewHandler(commands.NewService(commands.NewStore(pool), commandPublisher))
		defer commandConsumer.Close()
		go func() {
			if err := commandConsumer.Run(context.Background()); err != nil {
				slog.Error("hold command worker stopped", "error", err)
			}
		}()
		go func() {
			if err := holds.NewOutboxDispatcher(holds.NewRepository(pool), publisher).Run(context.Background(), 250*time.Millisecond); err != nil {
				slog.Error("outbox dispatcher stopped", "error", err)
			}
		}()
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
