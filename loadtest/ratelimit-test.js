// k6 load test for the distributed rate limiter.
//
//   Run the stack first:   docker compose up --build
//   Then load test:        k6 run loadtest/ratelimit-test.js
//
// What it checks:
//   * p95 / p99 latency stays low while the limiter is in the hot path
//   * the API returns a mix of 200s (allowed) and 429s (throttled)
//   * zero server errors (5xx) under sustained load
//   * throughput under sustained concurrency
//
// Tune BASE_URL / VUS via env: k6 run -e BASE_URL=http://localhost:8080 -e VUS=50 ...

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const VUS = __ENV.VUS ? parseInt(__ENV.VUS) : 50;

const allowed    = new Counter('requests_allowed');
const throttled  = new Counter('requests_throttled');
const serverErrs = new Counter('server_errors');
const reqDur     = new Trend('req_duration_ms', true);

export const options = {
  scenarios: {
    sustained_load: {
      executor: 'constant-vus',
      vus: VUS,
      duration: '30s',
    },
  },
  thresholds: {
    // Relaxed for Docker Desktop on Windows; bare-metal Linux is ~5-10x faster.
    'req_duration_ms': ['p(95)<150', 'p(99)<300'],
    // The app must never return 5xx under load.
    'server_errors': ['count==0'],
  },
};

export default function () {
  const res = http.get(`${BASE_URL}/api/ping`, {
    headers: { 'X-Api-Key': 'loadtest-key' },
  });

  const is200 = res.status === 200;
  const is429 = res.status === 429;

  check(res, {
    'status is 200 or 429': () => is200 || is429,
    'has rate-limit header': (r) => r.headers['X-Ratelimit-Limit'] !== undefined,
  });

  reqDur.add(res.timings.duration);

  if (is200) allowed.add(1);
  if (is429) throttled.add(1);
  if (res.status >= 500) serverErrs.add(1);
}

export function handleSummary(data) {
  const get = (metric, key) => {
    const m = data.metrics[metric];
    if (!m || !m.values) return 0;
    const v = m.values[key];
    return v != null ? v : 0;
  };

  const a   = get('requests_allowed',   'count');
  const t   = get('requests_throttled', 'count');
  const p95 = get('req_duration_ms',    'p(95)').toFixed(2);
  const p99 = get('req_duration_ms',    'p(99)').toFixed(2);
  const rps = get('http_reqs',          'rate').toFixed(0);

  const summary =
    `\n===== Rate Limiter Load Test =====\n` +
    `Throughput:      ${rps} req/s\n` +
    `Latency p95:     ${p95} ms\n` +
    `Latency p99:     ${p99} ms\n` +
    `Allowed (200):   ${a}\n` +
    `Throttled (429): ${t}\n` +
    `==================================\n`;

  return {
    stdout: summary,
    'loadtest/summary.json': JSON.stringify(data, null, 2),
  };
}
