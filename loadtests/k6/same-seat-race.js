// 500 different customers try to hold the SAME seat at the same instant.
// Pass criteria: exactly one 201, 499 x 409, zero other responses (no 5xx). The run script then audits the
// database to prove no seat is in two active reservations.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { customerTokens } from './lib/keycloak.js';

const GATEWAY = __ENV.GATEWAY || 'http://localhost:5250';
const CUSTOMERS = Number(__ENV.CUSTOMERS || 500);
const EVENT = '0197a000-0000-7000-8000-000000000002'; // "Load Test Night", 1,000 seats

const won = new Counter('holds_won');
const lost = new Counter('holds_lost');
const unexpected = new Counter('unexpected_responses');

export const options = {
  setupTimeout: '10m',
  scenarios: {
    race: { executor: 'per-vu-iterations', vus: CUSTOMERS, iterations: 1, maxDuration: '2m' },
  },
  thresholds: {
    holds_won: ['count==1'],
    holds_lost: [`count==${CUSTOMERS - 1}`],
    unexpected_responses: ['count==0'],
    // Sub-metrics per status code, so the summary says what went wrong if anything did.
    'unexpected_responses{status:500}': ['count>=0'],
    'unexpected_responses{status:503}': ['count>=0'],
    'unexpected_responses{status:502}': ['count>=0'],
    'unexpected_responses{status:0}': ['count>=0'],
  },
  summaryTrendStats: ['min', 'med', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const tokens = customerTokens(CUSTOMERS);
  const map = http.get(`${GATEWAY}/catalog/events/${EVENT}/seat-map`).json();
  const seat = map.sections.flatMap((s) => s.rows).flatMap((r) => r.seats).find((s) => s.status === 'Available');
  if (!seat) throw new Error('No available seat left; restart the stack for a fresh event.');
  // Every VU waits for the same wall-clock instant, so the requests really are concurrent.
  return { tokens, seatId: seat.seatId, label: seat.label, startAt: Date.now() + 5000 };
}

export default function (data) {
  const wait = (data.startAt - Date.now()) / 1000;
  if (wait > 0) sleep(wait);

  const res = http.post(
    `${GATEWAY}/booking/reservations`,
    JSON.stringify({ eventId: EVENT, seatIds: [data.seatId] }),
    { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${data.tokens[__VU - 1]}` }, tags: { name: 'hold' } },
  );

  if (res.status === 201) won.add(1);
  else if (res.status === 409) lost.add(1);
  else unexpected.add(1, { status: String(res.status) });
  check(res, { '201 or 409': (r) => r.status === 201 || r.status === 409 });
}

export function handleSummary(data) {
  const m = data.metrics;
  const hold = m['http_req_duration{name:hold}'] || m.http_req_duration;
  const report = {
    seat: data.setup_data ? data.setup_data.label : undefined,
    customers: CUSTOMERS,
    won: m.holds_won ? m.holds_won.values.count : 0,
    lost: m.holds_lost ? m.holds_lost.values.count : 0,
    unexpected: m.unexpected_responses ? m.unexpected_responses.values.count : 0,
    unexpectedByStatus: Object.fromEntries(Object.entries(m).filter(([k]) => k.startsWith('unexpected_responses{')).map(([k, v]) => [k, v.values.count])),
    latencyMs: hold.values,
    thresholdsPassed: Object.values(m).every((x) => !x.thresholds || Object.values(x.thresholds).every((t) => t.ok)),
  };
  return {
    stdout: `\n${JSON.stringify(report, null, 2)}\n`,
    '/results/same-seat-race.json': JSON.stringify(report, null, 2),
  };
}
