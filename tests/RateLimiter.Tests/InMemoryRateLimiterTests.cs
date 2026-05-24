using RateLimiter.Core;
using RateLimiter.Core.InMemory;
using Xunit;

namespace RateLimiter.Tests;

/// <summary>
/// Deterministic unit tests for the in-memory limiter using a controllable clock.
/// These exercise the same algorithm semantics the Redis Lua scripts implement.
/// </summary>
public class InMemoryRateLimiterTests
{
    private static (InMemoryRateLimiter limiter, Action<TimeSpan> advance) Build(RateLimitPolicy policy)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        InMemoryRateLimiter limiter = new(policy, () => now);
        void Advance(TimeSpan by) => now = now.Add(by);
        return (limiter, Advance);
    }

    [Theory]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindow)]
    public async Task Allows_up_to_the_limit_then_blocks(RateLimitAlgorithm algorithm)
    {
        var (limiter, _) = Build(new RateLimitPolicy
        {
            Algorithm = algorithm,
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1)
        });

        for (var i = 0; i < 5; i++)
        {
            var ok = await limiter.AcquireAsync("user-1");
            Assert.True(ok.Allowed, $"request {i + 1} should be allowed");
        }

        var blocked = await limiter.AcquireAsync("user-1");
        Assert.False(blocked.Allowed);
        Assert.Equal(0, blocked.Remaining);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindow)]
    public async Task Partitions_are_independent(RateLimitAlgorithm algorithm)
    {
        var (limiter, _) = Build(new RateLimitPolicy
        {
            Algorithm = algorithm,
            PermitLimit = 1,
            Window = TimeSpan.FromSeconds(1)
        });

        Assert.True((await limiter.AcquireAsync("a")).Allowed);
        Assert.True((await limiter.AcquireAsync("b")).Allowed);   // different key, own budget
        Assert.False((await limiter.AcquireAsync("a")).Allowed);  // first key exhausted
    }

    [Fact]
    public async Task FixedWindow_resets_after_window_elapses()
    {
        var (limiter, advance) = Build(new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            PermitLimit = 2,
            Window = TimeSpan.FromSeconds(1)
        });

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed);

        advance(TimeSpan.FromSeconds(1));

        Assert.True((await limiter.AcquireAsync("k")).Allowed); // window rolled over
    }

    [Fact]
    public async Task TokenBucket_refills_proportionally_over_time()
    {
        var (limiter, advance) = Build(new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.TokenBucket,
            PermitLimit = 10,
            Window = TimeSpan.FromSeconds(1) // refills 10 tokens/sec => 1 token per 100ms
        });

        for (var i = 0; i < 10; i++)
            Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed); // bucket empty

        advance(TimeSpan.FromMilliseconds(300)); // ~3 tokens refilled

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed); // only ~3 were available
    }

    [Fact]
    public async Task SlidingWindow_frees_capacity_as_old_entries_age_out()
    {
        var (limiter, advance) = Build(new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.SlidingWindow,
            PermitLimit = 3,
            Window = TimeSpan.FromSeconds(1)
        });

        for (var i = 0; i < 3; i++)
            Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed);

        advance(TimeSpan.FromMilliseconds(1001)); // all three entries age out

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
    }

    [Fact]
    public async Task Reports_remaining_permits()
    {
        var (limiter, _) = Build(new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.FixedWindow,
            PermitLimit = 3,
            Window = TimeSpan.FromSeconds(1)
        });

        Assert.Equal(2, (await limiter.AcquireAsync("k")).Remaining);
        Assert.Equal(1, (await limiter.AcquireAsync("k")).Remaining);
        Assert.Equal(0, (await limiter.AcquireAsync("k")).Remaining);
    }

    [Fact]
    public async Task Concurrent_callers_never_exceed_the_limit()
    {
        var (limiter, _) = Build(new RateLimitPolicy
        {
            Algorithm = RateLimitAlgorithm.TokenBucket,
            PermitLimit = 100,
            Window = TimeSpan.FromSeconds(1)
        });

        var tasks = Enumerable.Range(0, 500)
            .Select(_ => limiter.AcquireAsync("hot-key"))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var allowed = results.Count(r => r.Allowed);
        Assert.Equal(100, allowed); // exactly the capacity, no over-admission under contention
    }

    [Fact]
    public void Invalid_policy_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryRateLimiter(new RateLimitPolicy { PermitLimit = 0 }));
    }
}
