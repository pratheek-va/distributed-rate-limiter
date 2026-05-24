using RateLimiter.Core;
using RateLimiter.Core.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace RateLimiter.Tests;

/// <summary>
/// Integration tests that run the real Lua scripts against a real Redis (spun up in a container).
/// Requires a running Docker daemon; in CI this is provided automatically.
/// </summary>
public class RedisRateLimiterTests : IAsyncLifetime
{
#pragma warning disable CS0618 // RedisBuilder() ctor obsolete; WithImage still sets the image explicitly.
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();
#pragma warning restore CS0618

    private IConnectionMultiplexer _connection = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await _redis.DisposeAsync();
    }

    private RedisRateLimiter Limiter(RateLimitAlgorithm algorithm, long limit, TimeSpan window) =>
        new(_connection,
            new RateLimitPolicy { Algorithm = algorithm, PermitLimit = limit, Window = window },
            keyPrefix: Guid.NewGuid().ToString("N")); // isolate each test

    [Theory]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindow)]
    public async Task Allows_up_to_the_limit_then_blocks(RateLimitAlgorithm algorithm)
    {
        var limiter = Limiter(algorithm, limit: 5, window: TimeSpan.FromSeconds(10));

        for (var i = 0; i < 5; i++)
            Assert.True((await limiter.AcquireAsync("user")).Allowed, $"request {i + 1}");

        Assert.False((await limiter.AcquireAsync("user")).Allowed);
    }

    [Theory]
    [InlineData(RateLimitAlgorithm.TokenBucket)]
    [InlineData(RateLimitAlgorithm.FixedWindow)]
    [InlineData(RateLimitAlgorithm.SlidingWindow)]
    public async Task Two_instances_share_one_global_limit(RateLimitAlgorithm algorithm)
    {
        // Same policy + same prefix => simulate two app servers behind a load balancer.
        var policy = new RateLimitPolicy { Algorithm = algorithm, PermitLimit = 6, Window = TimeSpan.FromSeconds(10) };
        const string prefix = "shared-prefix";
        var serverA = new RedisRateLimiter(_connection, policy, prefix);
        var serverB = new RedisRateLimiter(_connection, policy, prefix);

        var allowed = 0;
        for (var i = 0; i < 10; i++)
        {
            var limiter = i % 2 == 0 ? serverA : serverB; // alternate servers
            if ((await limiter.AcquireAsync("global-user")).Allowed)
                allowed++;
        }

        Assert.Equal(6, allowed); // global cap held across both instances
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_the_limit()
    {
        var limiter = Limiter(RateLimitAlgorithm.SlidingWindow, limit: 50, window: TimeSpan.FromSeconds(10));

        var tasks = Enumerable.Range(0, 200)
            .Select(_ => limiter.AcquireAsync("hot"))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(50, results.Count(r => r.Allowed)); // atomic check-and-consume, no over-admission
    }

    [Fact]
    public async Task TokenBucket_refills_after_waiting()
    {
        var limiter = Limiter(RateLimitAlgorithm.TokenBucket, limit: 5, window: TimeSpan.FromSeconds(1));

        for (var i = 0; i < 5; i++)
            Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // ~2.5 tokens refill at 5/sec

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
    }

    [Fact]
    public async Task FixedWindow_resets_after_window()
    {
        var limiter = Limiter(RateLimitAlgorithm.FixedWindow, limit: 2, window: TimeSpan.FromMilliseconds(600));

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.True((await limiter.AcquireAsync("k")).Allowed);
        Assert.False((await limiter.AcquireAsync("k")).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(700));

        Assert.True((await limiter.AcquireAsync("k")).Allowed);
    }
}
