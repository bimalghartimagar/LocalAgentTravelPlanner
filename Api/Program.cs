using System.Threading.RateLimiting;
using LocalAgentTravelPlanner.Api.Middleware;
using LocalAgentTravelPlanner.Tools;
var builder = WebApplication.CreateBuilder(args);

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

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();
