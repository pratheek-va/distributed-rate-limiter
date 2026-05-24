using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RateLimiter.Core;
using RateLimiter.Core.Redis;
using StackExchange.Redis;

namespace RateLimiter.AspNetCore;

/// <summary>DI and pipeline helpers for wiring up the distributed rate limiter.</summary>
public static class RateLimiterExtensions
{
    /// <summary>Registers a Redis-backed distributed rate limiter and its middleware options.</summary>
    public static IServiceCollection AddRedisRateLimiter(
        this IServiceCollection services,
        string redisConnectionString,
        RateLimitPolicy policy,
        Action<RateLimitOptions>? configure = null)
    {
        policy.Validate();

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddSingleton<IRateLimiter>(
            sp => new RedisRateLimiter(sp.GetRequiredService<IConnectionMultiplexer>(), policy));

        return AddOptions(services, configure);
    }

    /// <summary>Registers a custom <see cref="IRateLimiter"/> (e.g. in-memory) and middleware options.</summary>
    public static IServiceCollection AddDistributedRateLimiter(
        this IServiceCollection services,
        Func<IServiceProvider, IRateLimiter> limiterFactory,
        Action<RateLimitOptions>? configure = null)
    {
        services.AddSingleton(limiterFactory);
        return AddOptions(services, configure);
    }

    /// <summary>Adds the rate-limiting middleware to the request pipeline.</summary>
    /// <remarks>Named to avoid clashing with the built-in <c>UseRateLimiter</c> in ASP.NET Core.</remarks>
    public static IApplicationBuilder UseDistributedRateLimiter(this IApplicationBuilder app)
        => app.UseMiddleware<RateLimitMiddleware>();

    private static IServiceCollection AddOptions(IServiceCollection services, Action<RateLimitOptions>? configure)
    {
        var options = new RateLimitOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        return services;
    }
}
