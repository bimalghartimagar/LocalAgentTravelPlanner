using System.Threading.RateLimiting;
using LocalAgentTravelPlanner.Api.Middleware;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Bootstrap logger for startup errors (before DI is available)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Bridge appsettings values into env vars so ChatClientFactory
    // (in the core project, no access to IConfiguration) can read them
    var anthropicKey = builder.Configuration["ANTHROPIC_API_KEY"];
    if (!string.IsNullOrWhiteSpace(anthropicKey)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", anthropicKey);
    }
    var geminiKey = builder.Configuration["GEMINI_API_KEY"];
    if (!string.IsNullOrWhiteSpace(geminiKey)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
    {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", geminiKey);
    }
    var groqKey = builder.Configuration["GROQ_API_KEY"];
    if (!string.IsNullOrWhiteSpace(groqKey)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GROQ_API_KEY")))
    {
        Environment.SetEnvironmentVariable("GROQ_API_KEY", groqKey);
    }
    var openRouterKey = builder.Configuration["OPEN_ROUTER_AI_KEY"];
    if (!string.IsNullOrWhiteSpace(openRouterKey)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPEN_ROUTER_AI_KEY")))
    {
        Environment.SetEnvironmentVariable("OPEN_ROUTER_AI_KEY", openRouterKey);
    }
    var ollamaModel = builder.Configuration["OLLAMA_MODEL"];
    if (!string.IsNullOrWhiteSpace(ollamaModel)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OLLAMA_MODEL")))
    {
        Environment.SetEnvironmentVariable("OLLAMA_MODEL", ollamaModel);
    }

    // Replace default logging with Serilog, configured from appsettings
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/api-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

    // Add services to the container
    builder.Services.AddControllers();
    builder.Services.AddHttpClient<ResearchTools>();
    builder.Services.AddHttpClient<TravelTools>();
    builder.Services.AddOpenApi();

    // Conversation persistence — SQLite by default (durable across restarts);
    // set Conversations:Backend=InMemory to opt into the volatile in-process store.
    var backend = builder.Configuration["Conversations:Backend"] ?? "Sqlite";
    if (string.Equals(backend, "InMemory", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
    }
    else
    {
        var connectionString = builder.Configuration.GetConnectionString("Conversations")
            ?? "Data Source=conversations.db";
        builder.Services.AddSingleton<IConversationStore>(
            _ => new SqliteConversationStore(connectionString));
    }

    // Per-conversation lock so two simultaneous turns on the same conversation can't race.
    builder.Services.AddSingleton<IConversationLock, ConversationLockService>();

    // OpenTelemetry tracing. Emits spans for AspNetCore requests, outbound HttpClient
    // calls (LLM providers + tool APIs), and the core service's own conversation.turn /
    // conversation.aggregator_phase activities. Ships via OTLP when
    // OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise only in-process listeners see spans.
    // Service name defaults to "LocalAgentTravelPlanner" and can be overridden via
    // OTEL_SERVICE_NAME.
    var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
        ?? builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
        ?? "LocalAgentTravelPlanner";
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName, serviceVersion: "1.0.0"))
        .WithTracing(t =>
        {
            t.AddSource(Diagnostics.ActivitySourceName)
             .AddAspNetCoreInstrumentation(opts =>
             {
                 // Skip health probes so traces don't get flooded by uptime checks.
                 opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                   && !ctx.Request.Path.StartsWithSegments("/api/travel/health");
             })
             .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                t.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
            }
        });

    // Rate limiting — protects LLM endpoints from abuse
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("plan", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.ContentType = "application/json";
            // Matches ApiError shape: code + error string. Client can branch on code.
            await context.HttpContext.Response.WriteAsync(
                """{"code":"rate.limited","error":"Too many requests. Try again in a minute."}""",
                cancellationToken);
        };
    });

    var app = builder.Build();

    // Trust forwarded headers from nginx (X-Forwarded-For, X-Forwarded-Proto)
    // Required for correct client IP in rate limiting and HTTPS detection
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    // Request logging — logs method, path, status code, elapsed time
    app.UseSerilogRequestLogging();

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    // Liveness + readiness endpoints for orchestrators (Docker, K8s, nginx upstream).
    // Neither requires the API key middleware — they answer before UseMiddleware runs.
    // /health/live: process is up. Cheap. For Docker HEALTHCHECK.
    // /health/ready: dependencies reachable (DB writable, at least one LLM provider
    // configured). For load-balancer routing decisions and blue/green cutovers.
    app.MapGet("/health/live", () => Results.Ok(new
    {
        status = "live",
        timestamp = DateTime.UtcNow
    }));

    app.MapGet("/health/ready", async (IConversationStore store, CancellationToken ct) =>
    {
        var checks = new Dictionary<string, object>();
        var allOk = true;

        // DB probe: create + delete a throwaway conversation. Exercises the write path
        // (schema present, permissions on file, WAL not locked) without polluting real data.
        try
        {
            var probe = await store.CreateAsync(ct);
            await store.DeleteAsync(probe.Id, ct);
            checks["db"] = "ok";
        }
        catch (Exception ex)
        {
            checks["db"] = new { status = "failing", error = ex.Message };
            allOk = false;
        }

        // Provider probe: readiness = at least one credential is present. Doesn't call
        // the provider (would be too expensive/slow); operators use /api/travel/health
        // for the live per-provider view.
        var anyProvider =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPEN_ROUTER_AI_KEY")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GROQ_API_KEY")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        checks["providers"] = anyProvider ? "at_least_one_configured" : "none_configured";
        if (!anyProvider) allOk = false;

        var payload = new
        {
            status = allOk ? "ready" : "degraded",
            timestamp = DateTime.UtcNow,
            checks
        };
        return allOk ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
    });

    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseRateLimiter();
    app.UseDefaultFiles();
    // Dev iteration: tell the browser not to cache app.js / index.html so a code change
    // picks up on a plain reload without needing the ?v= cache-bust. For production an
    // asset-hash build step would be the right answer, but the UI is hand-maintained here.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            var path = ctx.File.Name;
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
                ctx.Context.Response.Headers.Expires = "0";
            }
        }
    });
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
