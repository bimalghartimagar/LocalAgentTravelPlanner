using LocalAgentTravelPlanner.Tools;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddHttpClient<ResearchTools>();
builder.Services.AddHttpClient<TravelTools>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();
