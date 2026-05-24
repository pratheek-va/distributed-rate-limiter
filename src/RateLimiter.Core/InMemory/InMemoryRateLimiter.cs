using System.Collections.Concurrent;

namespace RateLimiter.Core.InMemory;

/// <summary>
/// Process-local rate limiter implementing the same three algorithms as <c>RedisRateLimiter</c>.
/// Useful for single-instance deployments, local development, and fast deterministic unit tests.
/// Not distributed: each process keeps its own counters.
/// </summary>
public sealed class InMemoryRateLimiter : IRateLimiter
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, State> _state = new();

    public RateLimitPolicy Policy { get; }

    /// <param name="clock">Optional clock override for deterministic testing.</param>
    public InMemoryRateLimiter(RateLimitPolicy policy, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        Policy = policy;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<RateLimitResult> AcquireAsync(
        string partitionKey, long permits = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
            throw new ArgumentException("Partition key must be provided.", nameof(partitionKey));
        if (permits <= 0)
            throw new ArgumentOutOfRangeException(nameof(permits), "Permits must be greater than zero.");

        var now = _clock().ToUnixTimeMilliseconds();
        var windowMs = (long)Policy.Window.TotalMilliseconds;
        var state = _state.GetOrAdd(partitionKey, _ => new State());

        lock (state)
        {
            return Task.FromResult(Policy.Algorithm switch
            {
                RateLimitAlgorithm.TokenBucket => TokenBucket(state, now, windowMs, permits),
                RateLimitAlgorithm.FixedWindow => FixedWindow(state, now, windowMs, permits),
                RateLimitAlgorithm.SlidingWindow => SlidingWindow(state, now, windowMs, permits),
                _ => throw new ArgumentOutOfRangeException()
            });
        }
    }

    private RateLimitResult TokenBucket(State s, long now, long windowMs, long permits)
    {
        var capacity = Policy.PermitLimit;
        var refillPerMs = (double)Policy.PermitLimit / windowMs;

        if (!s.Initialized)
        {
            s.Tokens = capacity;
            s.Timestamp = now;
            s.Initialized = true;
        }

        var elapsed = Math.Max(0, now - s.Timestamp);
        s.Tokens = Math.Min(capacity, s.Tokens + elapsed * refillPerMs);
        s.Timestamp = now;

        var allowed = s.Tokens >= permits;
        if (allowed) s.Tokens -= permits;

        var retryMs = allowed ? 0 : (long)Math.Ceiling((permits - s.Tokens) / refillPerMs);
        return Build(allowed, (long)Math.Floor(s.Tokens), retryMs, now);
    }

    private RateLimitResult FixedWindow(State s, long now, long windowMs, long permits)
    {
        if (!s.Initialized || now - s.Timestamp >= windowMs)
        {
            s.Timestamp = now;
            s.Count = 0;
            s.Initialized = true;
        }

        var allowed = s.Count + permits <= Policy.PermitLimit;
        if (allowed) s.Count += permits;

        var remaining = Math.Max(0, Policy.PermitLimit - s.Count);
        var retryMs = allowed ? 0 : s.Timestamp + windowMs - now;
        return Build(allowed, remaining, retryMs, now);
    }

    private RateLimitResult SlidingWindow(State s, long now, long windowMs, long permits)
    {
        var cutoff = now - windowMs;
        while (s.Log.Count > 0 && s.Log.Peek() <= cutoff)
            s.Log.Dequeue();

        var allowed = s.Log.Count + permits <= Policy.PermitLimit;
        if (allowed)
            for (var i = 0; i < permits; i++)
                s.Log.Enqueue(now);

        var remaining = Math.Max(0, Policy.PermitLimit - s.Log.Count);
        var retryMs = allowed || s.Log.Count == 0 ? 0 : s.Log.Peek() + windowMs - now;
        return Build(allowed, remaining, retryMs, now);
    }

    private RateLimitResult Build(bool allowed, long remaining, long retryMs, long now)
    {
        retryMs = Math.Max(0, retryMs);
        var retryAfter = TimeSpan.FromMilliseconds(retryMs);
        var nowOffset = DateTimeOffset.FromUnixTimeMilliseconds(now);
        var resetsAt = nowOffset + (allowed ? Policy.Window : retryAfter);
        return new RateLimitResult(allowed, Policy.PermitLimit, Math.Max(0, remaining), retryAfter, resetsAt);
    }

    private sealed class State
    {
        public bool Initialized;
        public double Tokens;
        public long Timestamp;
        public long Count;
        public readonly Queue<long> Log = new();
    }
}
