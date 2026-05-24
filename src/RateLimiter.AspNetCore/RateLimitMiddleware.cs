using Microsoft.AspNetCore.Http;
using RateLimiter.Core;

namespace RateLimiter.AspNetCore;

/// <summary>Throttles inbound requests using the configured <see cref="IRateLimiter"/> and partition strategy.</summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;
    private readonly RateLimitOptions _options;

    public RateLimitMiddleware(RequestDelegate next, IRateLimiter limiter, RateLimitOptions options)
    {
        _next = next;
        _limiter = limiter;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.Skip?.Invoke(context) == true)
        {
            await _next(context);
            return;
        }

        var partitionKey = _options.PartitionKeySelector(context);
        var result = await _limiter.AcquireAsync(partitionKey, permits: 1, context.RequestAborted);

        if (_options.EmitHeaders)
            AddHeaders(context, result);

        if (result.Allowed)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = _options.RejectionStatusCode;
        context.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(result.RetryAfter.TotalSeconds)).ToString();

        if (_options.OnRejected is not null)
        {
            await _options.OnRejected(context, result);
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = "Too many requests. Please retry later.",
            retryAfterSeconds = Math.Ceiling(result.RetryAfter.TotalSeconds)
        });
    }

    private static void AddHeaders(HttpContext context, RateLimitResult result)
    {
        var headers = context.Response.Headers;
        headers["X-RateLimit-Limit"] = result.Limit.ToString();
        headers["X-RateLimit-Remaining"] = result.Remaining.ToString();
        headers["X-RateLimit-Reset"] = result.ResetsAt.ToUnixTimeSeconds().ToString();
    }
}
