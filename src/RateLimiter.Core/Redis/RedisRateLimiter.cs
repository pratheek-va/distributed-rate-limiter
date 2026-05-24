using StackExchange.Redis;

namespace RateLimiter.Core.Redis;

/// <summary>
/// Distributed rate limiter backed by Redis. The check-and-consume runs as a single Lua script,
/// so it is atomic and consistent across every application instance sharing the Redis server.
/// </summary>
public sealed class RedisRateLimiter : IRateLimiter
{
    private readonly IDatabase _db;
    private readonly string _script;
    private readonly string _keyPrefix;

    public RateLimitPolicy Policy { get; }

    public RedisRateLimiter(IConnectionMultiplexer connection, RateLimitPolicy policy, string keyPrefix = "rl")
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        _db = connection.GetDatabase();
        Policy = policy;
        _script = LuaScripts.For(policy.Algorithm);
        _keyPrefix = keyPrefix;
    }

    public async Task<RateLimitResult> AcquireAsync(
        string partitionKey, long permits = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
            throw new ArgumentException("Partition key must be provided.", nameof(partitionKey));
        if (permits <= 0)
            throw new ArgumentOutOfRangeException(nameof(permits), "Permits must be greater than zero.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = new RedisKey($"{_keyPrefix}:{Policy.Algorithm}:{partitionKey}");
        var windowMs = (long)Policy.Window.TotalMilliseconds;

        RedisValue[] args = Policy.Algorithm switch
        {
            RateLimitAlgorithm.TokenBucket =>
            [
                Policy.PermitLimit,
                Policy.PermitLimit / Policy.Window.TotalMilliseconds, // refill tokens per ms
                now,
                permits
            ],
            RateLimitAlgorithm.SlidingWindow =>
            [
                Policy.PermitLimit,
                windowMs,
                now,
                permits,
                Guid.NewGuid().ToString("N") // unique member id to avoid sorted-set collisions
            ],
            _ => // FixedWindow
            [
                Policy.PermitLimit,
                windowMs,
                permits
            ]
        };

        var raw = (RedisValue[])(await _db.ScriptEvaluateAsync(_script, [key], args).ConfigureAwait(false))!;

        var allowed = (long)raw[0] == 1;
        var remaining = Math.Max(0, (long)raw[1]);
        var retryAfter = TimeSpan.FromMilliseconds(Math.Max(0, (long)raw[2]));
        var resetsAt = DateTimeOffset.UtcNow + (allowed ? Policy.Window : retryAfter);

        return new RateLimitResult(allowed, Policy.PermitLimit, remaining, retryAfter, resetsAt);
    }
}
