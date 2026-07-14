# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project files first for layer caching
COPY LocalAgentTravelPlanner.sln .
COPY LocalAgentTravelPlanner.csproj .
COPY Api/LocalAgentTravelPlanner.Api.csproj Api/
COPY Tests/LocalAgentTravelPlanner.Tests.csproj Tests/
RUN dotnet restore LocalAgentTravelPlanner.sln

# Copy everything and publish the API
COPY . .
RUN dotnet publish Api/LocalAgentTravelPlanner.Api.csproj \
    -c Release \
    -o /app \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -d /app appuser

# Create logs and data directories with correct ownership
RUN mkdir -p /app/logs /app/data && chown -R appuser:appuser /app/logs /app/data

COPY --from=build --chown=appuser:appuser /app .

USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Required: set via docker run -e or docker-compose
# ENV API_KEY=
# ENV ANTHROPIC_API_KEY=
# Or mount a file: ENV API_KEY_FILE=/run/secrets/api-key

ENTRYPOINT ["dotnet", "LocalAgentTravelPlanner.Api.dll"]
