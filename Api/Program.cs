using System.Threading.RateLimiting;
using LocalAgentTravelPlanner.Api.Middleware;
using LocalAgentTravelPlanner.Tools;
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
            await context.HttpContext.Response.WriteAsync(
                """{"error":"Too many requests. Try again in a minute."}""",
                cancellationToken);
        };
    });

    var app = builder.Build();

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

    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseRateLimiter();
    app.UseDefaultFiles();
    app.UseStaticFiles();
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
