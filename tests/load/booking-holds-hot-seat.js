import http from 'k6/http';
import { check } from 'k6';

// Report VUs, duration, payload, partition count, hot-seat ratio, accepted/rejected
// ratio, acknowledgement latency, and queue-drain time with every result.
export const options = { scenarios: { holds: { executor: 'constant-vus', vus: Number(__ENV.VUS || 20), duration: __ENV.DURATION || '30s' } } };
const baseUrl = __ENV.BOOKING_URL || 'http://localhost:8080';
const trip = __ENV.TRIP_ID || '40000000-0000-0000-0000-000000000001';
const carriage = __ENV.CARRIAGE_ID || 'A';
const owner = __ENV.OWNER_ID || '50000000-0000-0000-0000-000000000001';

export default function () {
  const hot = Math.random() < Number(__ENV.HOT_SEAT_RATIO || 0.8);
  const seat = hot ? '70000000-0000-0000-0000-000000000001' : `70000000-0000-0000-0000-${String(__VU * 10000 + __ITER).padStart(12, '0')}`;
  const response = http.post(`${baseUrl}/api/holds`, JSON.stringify({ segments: [{ trip_id: trip, carriage_id: carriage, seat_id: seat, segment_index: 1 }] }), { headers: { 'Content-Type': 'application/json', 'Idempotency-Key': `${__VU}-${__ITER}`, 'X-User-Id': owner } });
  check(response, { 'durably accepted or rejected': (r) => r.status === 202 || r.status === 409 });
}
