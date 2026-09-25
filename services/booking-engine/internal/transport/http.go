package transport

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/commands"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
	"net/http"
	"time"
)

type holdService interface {
	Submit(context.Context, string, string, string, time.Time, []holds.Segment) (commands.Result, error)
	Get(context.Context, string) (commands.Result, error)
	GetHold(context.Context, string) (holds.Hold, error)
}

func NewHandler(services ...holdService) http.Handler {
	return newHandler(false, services...)
}

// NewDevelopmentHandler exposes Kafka command diagnostics only when the caller
// has explicitly enabled them for a local development process.
func NewDevelopmentHandler(enabled bool, services ...holdService) http.Handler {
	return newHandler(enabled, services...)
}

func newHandler(diagnostics bool, services ...holdService) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", health)
	if len(services) > 0 {
		mux.HandleFunc("POST /api/holds", postHold(services[0]))
		mux.HandleFunc("GET /api/booking-requests/{id}", getRequest(services[0]))
		mux.HandleFunc("GET /api/holds/{id}", getHold(services[0]))
		if diagnostics {
			mux.HandleFunc("GET /internal/kafka/hold-commands", getDiagnosticHold(services[0]))
		}
	}
	return mux
}
func getDiagnosticHold(s holdService) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		tripID := "40000000-0000-0000-0000-000000000001"
		ownerID := holds.NewID()
		out, err := s.Submit(r.Context(), ownerID, holds.NewID(), holds.NewID(), time.Now().UTC().Add(10*time.Minute), []holds.Segment{{TripID: tripID, CarriageID: "A", SeatID: holds.NewID(), Index: 1}})
		if err != nil {
			http.Error(w, err.Error(), http.StatusServiceUnavailable)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusAccepted)
		_ = json.NewEncoder(w).Encode(out)
	}
}
func getHold(s holdService) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		result, err := s.GetHold(r.Context(), r.PathValue("id"))
		if err != nil {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(result)
	}
}

type holdInput struct {
	Segments []holds.Segment `json:"segments"`
}

func postHold(s holdService) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		var in holdInput
		decoder := json.NewDecoder(r.Body)
		decoder.DisallowUnknownFields()
		if decoder.Decode(&in) != nil || len(in.Segments) < 1 || len(in.Segments) > 4 {
			http.Error(w, "invalid hold request", 400)
			return
		}
		key, owner := r.Header.Get("Idempotency-Key"), r.Header.Get("X-User-Id")
		if key == "" || owner == "" {
			http.Error(w, "Idempotency-Key and X-User-Id are required", 400)
			return
		}
		out, err := s.Submit(r.Context(), owner, key, r.Header.Get("X-Correlation-Id"), time.Now().UTC().Add(10*time.Minute), in.Segments)
		if err != nil {
			if errors.Is(err, commands.ErrIdempotencyConflict) {
				http.Error(w, "idempotency key was used with a different request", http.StatusConflict)
				return
			}
			http.Error(w, err.Error(), 409)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(202)
		_ = json.NewEncoder(w).Encode(out)
	}
}
func getRequest(s holdService) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		out, err := s.Get(r.Context(), r.PathValue("id"))
		if err != nil {
			http.NotFound(w, r)
			return
		}
		_ = json.NewEncoder(w).Encode(out)
	}
}

func health(response http.ResponseWriter, _ *http.Request) {
	response.Header().Set("Content-Type", "application/json")
	response.WriteHeader(http.StatusOK)
	_ = json.NewEncoder(response).Encode(map[string]string{
		"service": "booking-engine",
		"status":  "healthy",
	})
}
