import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

// Report VUs, duration, payload, partition count, hot-seat ratio, accepted/rejected
// ratio, acknowledgement latency, and queue-drain time with every result.
export const options = { scenarios: { holds: { executor: 'constant-vus', vus: Number(__ENV.VUS || 20), duration: __ENV.DURATION || '30s' } } };
const baseUrl = __ENV.BOOKING_URL || 'http://localhost:8080';
const trip = __ENV.TRIP_ID || '40000000-0000-0000-0000-000000000001';
const carriage = __ENV.CARRIAGE_ID || 'A';
const owner = __ENV.OWNER_ID || '50000000-0000-0000-0000-000000000001';
const runId = __ENV.RUN_ID || `${Date.now()}`;
const accepted = new Counter('hold_commands_accepted');
const rejected = new Counter('hold_commands_rejected');
const unexpected = new Counter('hold_commands_unexpected');
const held = new Counter('hold_results_held');
const contended = new Counter('hold_results_rejected');
const incomplete = new Counter('hold_results_incomplete');
const completion = new Trend('hold_completion_ms', true);

export default function () {
  const started = Date.now();
  const hot = Math.random() < Number(__ENV.HOT_SEAT_RATIO || 0.8);
  const seat = hot ? '70000000-0000-0000-0000-000000000001' : `70000000-0000-0000-0000-${String(__VU * 10000 + __ITER).padStart(12, '0')}`;
  const response = http.post(`${baseUrl}/api/holds`, JSON.stringify({ segments: [{ trip_id: trip, carriage_id: carriage, seat_id: seat, segment_index: 1 }] }), { headers: { 'Content-Type': 'application/json', 'Idempotency-Key': `${runId}-${__VU}-${__ITER}`, 'X-User-Id': owner } });
  if (response.status === 202) {
    accepted.add(1);
  } else if (response.status === 409) {
    rejected.add(1);
  } else {
    unexpected.add(1);
  }
  check(response, { 'durably accepted': (r) => r.status === 202 });
  if (response.status !== 202 || __ENV.POLL_RESULTS !== 'true') {
    return;
  }
  const requestId = response.json('request_id');
  const deadline = Date.now() + Number(__ENV.POLL_TIMEOUT_MS || 15000);
  while (Date.now() < deadline) {
    const result = http.get(`${baseUrl}/api/booking-requests/${requestId}`, {
      headers: { 'X-User-Id': owner },
      tags: { name: 'booking request status' },
    });
    if (result.status === 200) {
      const status = result.json('status');
      if (status === 'HELD' || status === 'REJECTED') {
        (status === 'HELD' ? held : contended).add(1);
        completion.add(Date.now() - started);
        return;
      }
    }
    sleep(0.1);
  }
  incomplete.add(1);
}
