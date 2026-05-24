namespace RateLimiter.Core;

/// <summary>A rate limiter that atomically decides whether a partition key may consume permits.</summary>
public interface IRateLimiter
{
    /// <summary>The policy this limiter enforces.</summary>
    RateLimitPolicy Policy { get; }

    /// <summary>
    /// Attempts to consume <paramref name="permits"/> for <paramref name="partitionKey"/>.
    /// The check-and-consume is atomic, so it is safe across many concurrent callers and processes.
    /// </summary>
    Task<RateLimitResult> AcquireAsync(string partitionKey, long permits = 1, CancellationToken cancellationToken = default);
}
