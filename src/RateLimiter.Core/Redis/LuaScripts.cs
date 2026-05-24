using System.Reflection;

namespace RateLimiter.Core.Redis;

/// <summary>Loads the embedded Lua scripts once and exposes them per algorithm.</summary>
internal static class LuaScripts
{
    private static readonly string TokenBucket = Load("token_bucket.lua");
    private static readonly string SlidingWindow = Load("sliding_window.lua");
    private static readonly string FixedWindow = Load("fixed_window.lua");

    public static string For(RateLimitAlgorithm algorithm) => algorithm switch
    {
        RateLimitAlgorithm.TokenBucket => TokenBucket,
        RateLimitAlgorithm.SlidingWindow => SlidingWindow,
        RateLimitAlgorithm.FixedWindow => FixedWindow,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown rate limit algorithm.")
    };

    private const string ResourcePrefix = "RateLimiter.Core.Lua.";

    private static string Load(string fileName)
    {
        var assembly = typeof(LuaScripts).Assembly;
        var resourceName = ResourcePrefix + fileName;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Lua script '{resourceName}' was not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
