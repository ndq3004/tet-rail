package holds

import (
	"context"
	"errors"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestExpiryWorkerRetriesAndReportsCommittedWork(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	calls := 0
	worker := ExpiryWorker{reconcile: func(ctx context.Context, now time.Time, limit int) (int, error) {
		calls++
		if _, ok := ctx.Deadline(); !ok || now.IsZero() || limit != 5 {
			t.Fatal("missing reconciliation deadline, server time, or batch limit")
		}
		if calls == 1 {
			return 2, errors.New("test database unavailable")
		}
		cancel()
		return 1, nil
	}}
	worker.Run(ctx, time.Millisecond, 5)
	response := httptest.NewRecorder()
	worker.ServeHTTP(response, httptest.NewRequest("GET", "/metrics", nil))
	if calls != 2 || !strings.Contains(response.Body.String(), "tetrail_booking_expiry_errors_total 1\n") ||
		!strings.Contains(response.Body.String(), "tetrail_booking_expiry_reconciled_total 3\n") {
		t.Fatalf("calls=%d response=%q", calls, response.Body.String())
	}
}
