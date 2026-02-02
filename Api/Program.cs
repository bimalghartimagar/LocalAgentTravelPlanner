var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Add CORS for web clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapControllers();

// Simple root endpoint
app.MapGet("/", () => new
{
    Name = "Travel Planner API",
    Version = "1.0",
    Endpoints = new
    {
        Health = "GET /api/travel/health",
        CreatePlan = "POST /api/travel/plan",
        StreamPlan = "GET /api/travel/plan/stream?request=..."
    }
});

app.Run();
