using Microsoft.AspNetCore.Http;
using RateLimiter.Core;

namespace RateLimiter.AspNetCore;

/// <summary>Configures how the middleware partitions traffic and responds when a request is throttled.</summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Selects the partition key a request is counted against. Defaults to the client IP.
    /// Swap for an API key, user id, or tenant to limit per-caller instead of per-IP.
    /// </summary>
    public Func<HttpContext, string> PartitionKeySelector { get; set; } =
        ctx => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Return true to bypass rate limiting for a request (e.g. health checks, internal traffic).</summary>
    public Func<HttpContext, bool>? Skip { get; set; }

    /// <summary>Status code returned when a request is throttled. Defaults to 429.</summary>
    public int RejectionStatusCode { get; set; } = StatusCodes.Status429TooManyRequests;

    /// <summary>Emit X-RateLimit-Limit / X-RateLimit-Remaining / X-RateLimit-Reset headers.</summary>
    public bool EmitHeaders { get; set; } = true;

    /// <summary>Optional custom response for throttled requests. When null, a small JSON body is written.</summary>
    public Func<HttpContext, RateLimitResult, Task>? OnRejected { get; set; }
}
