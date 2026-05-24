using RateLimiter.AspNetCore;
using RateLimiter.Core;

var builder = WebApplication.CreateBuilder(args);

// Redis connection: env var wins (used by docker-compose), then appsettings, then localhost.
var redisConnection =
    Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    ?? builder.Configuration["Redis:ConnectionString"]
    ?? "localhost:6379";

var policy = new RateLimitPolicy
{
    Algorithm = Enum.Parse<RateLimitAlgorithm>(
        builder.Configuration["RateLimit:Algorithm"] ?? nameof(RateLimitAlgorithm.TokenBucket)),
    PermitLimit = builder.Configuration.GetValue<long?>("RateLimit:PermitLimit") ?? 10,
    Window = TimeSpan.FromSeconds(builder.Configuration.GetValue<double?>("RateLimit:WindowSeconds") ?? 1)
};

builder.Services.AddRedisRateLimiter(redisConnection, policy, options =>
{
    // Limit per API key when supplied, otherwise per client IP.
    options.PartitionKeySelector = ctx =>
        ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) && !string.IsNullOrWhiteSpace(key)
            ? $"key:{key}"
            : $"ip:{ctx.Connection.RemoteIpAddress}";

    // Never throttle health checks.
    options.Skip = ctx => ctx.Request.Path.StartsWithSegments("/health");
});

var app = builder.Build();

// Identifies which instance served the request (proves limits hold across instances behind the LB).
var instanceId = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? Environment.MachineName;

app.UseDistributedRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", instance = instanceId }));

app.MapGet("/", () => Results.Ok(new
{
    service = "distributed-rate-limiter-demo",
    instance = instanceId,
    policy = new { policy.Algorithm, policy.PermitLimit, windowSeconds = policy.Window.TotalSeconds }
}));

app.MapGet("/api/ping", () => Results.Ok(new
{
    message = "pong",
    instance = instanceId,
    servedAt = DateTimeOffset.UtcNow
}));

app.Run();

// Exposed so integration tests can spin the app up with WebApplicationFactory.
public partial class Program;
