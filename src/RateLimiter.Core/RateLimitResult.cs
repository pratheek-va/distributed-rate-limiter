namespace RateLimiter.Core;

/// <summary>Outcome of a single <see cref="IRateLimiter.AcquireAsync"/> call.</summary>
/// <param name="Allowed">True when the request is permitted.</param>
/// <param name="Limit">The configured permit limit (for the X-RateLimit-Limit header).</param>
/// <param name="Remaining">Permits remaining in the current window/bucket.</param>
/// <param name="RetryAfter">How long the caller should wait before retrying when blocked (zero when allowed).</param>
/// <param name="ResetsAt">When the limit fully resets / the bucket refills.</param>
public readonly record struct RateLimitResult(
    bool Allowed,
    long Limit,
    long Remaining,
    TimeSpan RetryAfter,
    DateTimeOffset ResetsAt);
