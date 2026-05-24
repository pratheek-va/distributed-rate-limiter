namespace RateLimiter.Core;

/// <summary>
/// Defines how many permits are allowed over a window for a single partition key.
/// </summary>
public sealed class RateLimitPolicy
{
    /// <summary>Algorithm used to enforce the limit.</summary>
    public RateLimitAlgorithm Algorithm { get; init; } = RateLimitAlgorithm.TokenBucket;

    /// <summary>
    /// Maximum permits allowed. For token bucket this is the bucket capacity (max burst);
    /// for fixed/sliding window it is the number of requests allowed per <see cref="Window"/>.
    /// </summary>
    public long PermitLimit { get; init; } = 100;

    /// <summary>
    /// Time window. For fixed/sliding window this is the window length; for token bucket it is the
    /// period over which a full bucket (<see cref="PermitLimit"/> tokens) is refilled.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (PermitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(PermitLimit), "PermitLimit must be greater than zero.");
        if (Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Window), "Window must be greater than zero.");
    }
}
