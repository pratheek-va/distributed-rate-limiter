# Distributed Rate Limiter

A production-style **distributed rate limiter** for .NET 8, backed by Redis. The check-and-consume
runs as an **atomic Lua script**, so a single global limit is enforced correctly across many
application instances behind a load balancer — no over-admission under concurrency.

Ships with three algorithms (**token bucket, sliding-window log, fixed window**), drop-in
**ASP.NET Core middleware**, a runnable demo (2 API instances + nginx + Redis via Docker Compose),
**k6 load tests**, and unit + Redis integration tests.

> Built to demonstrate distributed-systems fundamentals: atomicity, consistency across nodes,
> and measured performance under load.

---

## Why this is non-trivial

A naive limiter does `GET` → check → `SET`. Across concurrent requests (or multiple servers) that
read-modify-write **races**, letting callers exceed the limit. This implementation pushes the entire
decision into Redis as a Lua script, which Redis executes **atomically** — so the limit holds exactly,
even with hundreds of concurrent requests spread over multiple instances.

## Architecture

```
                       ┌──────────────────┐
   clients ───────────▶│  nginx (LB :8080)│
                       └────────┬─────────┘
                   round-robin  │
                ┌───────────────┴───────────────┐
                ▼                                ▼
        ┌──────────────┐                 ┌──────────────┐
        │  API api1     │                 │  API api2     │
        │  middleware   │                 │  middleware   │
        └──────┬───────┘                 └───────┬───────┘
               │   atomic EVALSHA (Lua)          │
               └───────────────┬─────────────────┘
                               ▼
                       ┌────────────────┐
                       │     Redis      │  ← single source of truth for counters
                       └────────────────┘
```

Both API instances share one Redis. Because the counter update is atomic, the **global** limit is
enforced regardless of which instance a request lands on.

## Algorithms

| Algorithm          | Storage in Redis            | Best for                              | Trade-off                           |
|--------------------|-----------------------------|---------------------------------------|-------------------------------------|
| **Token bucket**   | hash `{tokens, ts}`         | smooth rate + controlled bursts       | approximate, very cheap             |
| **Sliding window** | sorted set of timestamps    | accurate "N per rolling window"       | more memory (one entry per request) |
| **Fixed window**   | single integer counter      | cheapest, simplest                    | allows 2× burst at window edges     |

All three return `{ allowed, remaining, retryAfterMs }` and live in
[`src/RateLimiter.Core/Lua/`](src/RateLimiter.Core/Lua/).

## Quick start (Docker)

```bash
docker compose up --build
# API + nginx on http://localhost:8080, Redis on :6379

curl -i http://localhost:8080/api/ping          # 200, see X-RateLimit-* headers
for i in $(seq 1 20); do curl -s -o /dev/null -w "%{http_code} " \
  -H "X-Api-Key: demo" http://localhost:8080/api/ping; done
# => 200 200 ... 429 429   (limit hit; note it holds across api1 AND api2)
```

`/api/ping` returns which instance served it, so you can see the limit holding across both.

## Use as a library

```csharp
// Program.cs
builder.Services.AddRedisRateLimiter(
    redisConnectionString: "localhost:6379",
    policy: new RateLimitPolicy
    {
        Algorithm   = RateLimitAlgorithm.TokenBucket,
        PermitLimit = 100,                       // burst capacity
        Window      = TimeSpan.FromSeconds(1)    // refilled fully each second
    },
    configure: options =>
    {
        // limit per API key, fall back to client IP
        options.PartitionKeySelector = ctx =>
            ctx.Request.Headers.TryGetValue("X-Api-Key", out var k)
                ? $"key:{k}" : $"ip:{ctx.Connection.RemoteIpAddress}";
        options.Skip = ctx => ctx.Request.Path.StartsWithSegments("/health");
    });

var app = builder.Build();
app.UseDistributedRateLimiter();   // 429 + Retry-After + X-RateLimit-* headers
```

Or call the limiter directly:

```csharp
var result = await rateLimiter.AcquireAsync(partitionKey: "user-42");
if (!result.Allowed)
    return Results.StatusCode(429); // result.RetryAfter tells the caller when to retry
```

No Redis? The same `IRateLimiter` has an in-memory implementation
(`InMemoryRateLimiter`) for single-instance apps, local dev, and tests.

## Load testing

```bash
docker compose up --build -d
k6 run loadtest/ratelimit-test.js          # or: k6 run -e VUS=100 loadtest/ratelimit-test.js
```

The script drives sustained concurrent traffic at one partition key and asserts latency thresholds
(`p95 < 25ms`, `p99 < 50ms`) while reporting the allowed/throttled split. It prints a summary like:

```
===== Rate Limiter Load Test =====
Throughput:      <rps>  req/s
Latency p95:     <p95>  ms
Latency p99:     <p99>  ms
Allowed (200):   <n>
Throttled (429): <n>
==================================
```

> **Fill in your measured numbers here after running** — these are the figures recruiters care about.
> | Metric | Result |
> |---|---|
> | Throughput | _e.g._ 8,200 req/s |
> | Latency p95 | _e.g._ 9 ms |
> | Latency p99 | _e.g._ 18 ms |

## Tests

```bash
dotnet test
```

- **Unit tests** (`InMemoryRateLimiterTests`) — deterministic, clock-injected; run anywhere.
- **Integration tests** (`RedisRateLimiterTests`) — run the real Lua scripts against a real Redis via
  [Testcontainers](https://dotnet.testcontainers.org/) (needs Docker). Includes a test proving
  **two limiter instances share one global budget** and that concurrent requests never over-admit.

CI ([GitHub Actions](.github/workflows/ci.yml)) builds and runs both suites on every push.

## Project structure

```
src/
  RateLimiter.Core/         # algorithms, Redis limiter, atomic Lua scripts, in-memory limiter
  RateLimiter.AspNetCore/   # middleware + DI extensions
demo/DemoApi/               # sample API protected by the limiter (+ Dockerfile)
tests/RateLimiter.Tests/    # unit + Testcontainers integration tests
loadtest/                   # k6 load test
docker-compose.yml          # redis + 2 API instances + nginx LB
```

## Design decisions

- **Atomicity via Lua, not WATCH/MULTI** — one round trip, no retry loop, no race window.
- **`EVALSHA` caching** — StackExchange.Redis hashes the script and uses `EVALSHA`, so the script body
  is sent once, not per request.
- **Key expiry on every write** — idle keys self-clean, so memory tracks active traffic, not history.
- **Pluggable partition key** — limit per IP, API key, user, or tenant by swapping one function.
- **`IRateLimiter` abstraction** — Redis for distributed; in-memory for single-node/tests, same API.

## License

MIT
# distributed-rate-limiter
# distributed-rate-limiter
