package transport

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/commands"
	"github.com/tetrail/train-ticket-online/services/booking-engine/internal/holds"
)

type fakeHoldService struct {
	segments []holds.Segment
	err      error
	owner    string
}

func (f *fakeHoldService) Submit(_ context.Context, _, _, _ string, _ time.Time, segments []holds.Segment) (commands.Result, error) {
	f.segments = segments
	return commands.Result{RequestID: "request-1", Status: "QUEUED"}, f.err
}
func (f *fakeHoldService) Get(context.Context, string) (commands.Result, error) {
	return commands.Result{OwnerID: f.owner, Status: "HELD"}, nil
}
func (f *fakeHoldService) GetHold(context.Context, string) (holds.Hold, error) {
	return holds.Hold{OwnerID: f.owner, State: "ACTIVE"}, nil
}

func TestBookingLookupsRequireOwnership(t *testing.T) {
	handler := NewHandler(&fakeHoldService{owner: "customer"})
	for _, path := range []string{"/api/holds/id", "/api/booking-requests/id"} {
		for _, tc := range []struct {
			owner  string
			status int
		}{{"", 404}, {"other", 404}, {"customer", 200}} {
			request := httptest.NewRequest(http.MethodGet, path, nil)
			request.Header.Set("X-User-Id", tc.owner)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != tc.status {
				t.Errorf("lookup %s as %q returned %d, want %d", path, tc.owner, response.Code, tc.status)
			}
		}
	}
}

func TestHealth(t *testing.T) {
	request := httptest.NewRequest(http.MethodGet, "/health", nil)
	response := httptest.NewRecorder()

	NewHandler().ServeHTTP(response, request)

	if response.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, response.Code)
	}
}

func TestPostHoldAcceptsDocumentedSnakeCasePayload(t *testing.T) {
	service := &fakeHoldService{}
	request := httptest.NewRequest(http.MethodPost, "/api/holds", strings.NewReader(`{"segments":[{"trip_id":"trip","carriage_id":"A","seat_id":"seat","segment_index":1}]}`))
	request.Header.Set("Idempotency-Key", "key")
	request.Header.Set("X-User-Id", "owner")
	response := httptest.NewRecorder()

	NewHandler(service).ServeHTTP(response, request)

	if response.Code != http.StatusAccepted {
		t.Fatalf("expected status %d, got %d: %s", http.StatusAccepted, response.Code, response.Body.String())
	}
	if len(service.segments) != 1 || service.segments[0].CarriageID != "A" || service.segments[0].Index != 1 {
		t.Fatalf("decoded segments = %#v", service.segments)
	}
	var result commands.Result
	if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil || result.RequestID != "request-1" {
		t.Fatalf("response = %q, decode error = %v", response.Body.String(), err)
	}
}

func TestPostHoldRejectsUnknownFields(t *testing.T) {
	request := httptest.NewRequest(http.MethodPost, "/api/holds", strings.NewReader(`{"segments":[],"unexpected":true}`))
	request.Header.Set("Idempotency-Key", "key")
	request.Header.Set("X-User-Id", "owner")
	response := httptest.NewRecorder()

	NewHandler(&fakeHoldService{}).ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, response.Code)
	}
}
