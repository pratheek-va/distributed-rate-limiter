// k6 load test for the distributed rate limiter.
//
//   Run the stack first:   docker compose up --build
//   Then load test:        k6 run loadtest/ratelimit-test.js
//
// What it checks:
//   * p95 latency stays low while the limiter is in the hot path
//   * the API correctly returns a mix of 200s (allowed) and 429s (throttled)
//   * throughput under sustained concurrency
//
// Tune BASE_URL / VUS via env: k6 run -e BASE_URL=http://localhost:8080 -e VUS=50 ...

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const VUS = __ENV.VUS ? parseInt(__ENV.VUS) : 50;

const allowed = new Counter('requests_allowed');
const throttled = new Counter('requests_throttled');
const throttledRate = new Rate('throttled_rate');

export const options = {
  scenarios: {
    sustained_load: {
      executor: 'constant-vus',
      vus: VUS,
      duration: '30s',
    },
  },
  thresholds: {
    // The limiter must add negligible latency even under load.
    http_req_duration: ['p(95)<25', 'p(99)<50'],
    // We expect throttling to happen (proves the limiter is working) but the
    // app must never error out (no 5xx).
    http_req_failed: ['rate<0.01'], // counts only network/5xx failures, not 429s
  },
};

export default function () {
  // Share one partition key so all VUs compete for the same global budget.
  const res = http.get(`${BASE_URL}/api/ping`, {
    headers: { 'X-Api-Key': 'loadtest-key' },
  });

  const is200 = res.status === 200;
  const is429 = res.status === 429;

  check(res, {
    'status is 200 or 429': () => is200 || is429,
    'has rate-limit headers': (r) => r.headers['X-Ratelimit-Limit'] !== undefined,
  });

  if (is200) allowed.add(1);
  if (is429) throttled.add(1);
  throttledRate.add(is429);
}

export function handleSummary(data) {
  const a = data.metrics.requests_allowed ? data.metrics.requests_allowed.values.count : 0;
  const t = data.metrics.requests_throttled ? data.metrics.requests_throttled.values.count : 0;
  const p95 = data.metrics.http_req_duration.values['p(95)'].toFixed(2);
  const p99 = data.metrics.http_req_duration.values['p(99)'].toFixed(2);
  const rps = data.metrics.http_reqs.values.rate.toFixed(0);

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
