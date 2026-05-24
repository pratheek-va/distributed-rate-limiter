namespace RateLimiter.Core;

/// <summary>Rate-limiting strategy used to decide whether a request is allowed.</summary>
public enum RateLimitAlgorithm
{
    /// <summary>Counts requests in a window that expires from the first request. Cheapest, allows bursts at window edges.</summary>
    FixedWindow,

    /// <summary>Sliding window log (sorted set of timestamps). Most accurate, slightly higher memory.</summary>
    SlidingWindow,

    /// <summary>Token bucket: smooth refill that permits controlled bursts up to a capacity.</summary>
    TokenBucket
}
