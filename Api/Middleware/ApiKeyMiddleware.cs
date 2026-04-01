namespace LocalAgentTravelPlanner.Api.Middleware;

/// <summary>
/// Validates X-Api-Key header against the configured API key.
///
/// Key resolution order:
/// 1. IConfiguration "ApiKey" (appsettings.json, user secrets, env vars)
/// 2. File path from IConfiguration "ApiKeyFile"
/// 3. Fail fast at startup
///
/// Always enforced — app won't start without a key.
/// Skips auth for static files and the health endpoint.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _apiKey = LoadApiKey(configuration);
    }

    private static string LoadApiKey(IConfiguration configuration)
    {
        // 1. Config value (appsettings.json, user secrets, or env var API_KEY via config binding)
        var key = configuration["ApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
            return key.Trim();

        // 2. File path (Docker secrets, mounted volumes)
        var filePath = configuration["ApiKeyFile"];
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"ApiKeyFile points to '{filePath}' which does not exist.");

            key = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrWhiteSpace(key))
                return key;

            throw new InvalidOperationException($"ApiKeyFile '{filePath}' is empty.");
        }

        throw new InvalidOperationException(
            "API key not configured. Set 'ApiKey' in appsettings.json, user secrets, environment variable, or 'ApiKeyFile' pointing to a file containing the key.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for static files and non-API routes
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Skip auth for health endpoint — always public
        if (path.Equals("/api/travel/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Validate the key
        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || !string.Equals(_apiKey, providedKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected request to {Path} — invalid or missing API key", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":"Invalid or missing API key"}""");
            return;
        }

        await _next(context);
    }
}
