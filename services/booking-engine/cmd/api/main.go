package main

import (
	"log/slog"
	"net/http"
	"os"

	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/transport"
)

func main() {
	address := os.Getenv("HTTP_ADDRESS")
	if address == "" {
		address = ":8080"
	}

	server := &http.Server{
		Addr:    address,
		Handler: transport.NewHandler(),
	}

	slog.Info("booking engine listening", "address", address)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		slog.Error("booking engine stopped", "error", err)
		os.Exit(1)
	}
}

