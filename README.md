# Local Agent Travel Planner

A multi-agent AI travel planning system built with .NET 10 and the [Microsoft Agent Framework (MAF)](https://github.com/microsoft/agents). Five specialized agents collaborate sequentially to research a destination, build an itinerary, calculate a budget, audit the plan against six quality criteria, and present a polished result.

The system supports both local Ollama models and the Anthropic Claude API through a shared `IChatClient` abstraction, so you can develop offline and switch to a hosted model with a single config change.

![Multi-agent pipeline](docs/pipeline.svg)

## Features

- **Sequential 5-agent pipeline** — Researcher → Planner → Accountant → Auditor → Aggregator
- **Provider-swappable LLMs** — Ollama (local) or Anthropic Claude, auto-detected from environment
- **Live API integration** — Nominatim geocoding, Open-Meteo weather, OpenTripMap POIs, OSRM routing
- **Deterministic validation** — budget math, travel-time feasibility, safety, groundedness, completeness
- **Real-time browser UI** — Server-Sent Events stream per-agent progress with throttled Markdown rendering
- **Production hardening** — API key auth, rate limiting, HTTPS redirect, structured logging (Serilog)
- **Prompt injection guardrails** — pre-pipeline intent classifier + per-agent scope enforcement
- **Dockerized deployment** — multi-stage build with nginx reverse proxy
- **Quality evaluation** — `Microsoft.Extensions.AI.Evaluation` with 6 LLM-as-judge metrics

## Quick Start

### Option 1: Docker (recommended)

```bash
# Copy the env template
cp .env.example .env

# Edit .env — set API_KEY (required) and optionally ANTHROPIC_API_KEY
# If no Anthropic key is set, the system falls back to a local Ollama instance

# Start the stack (app + nginx)
docker compose up -d

# Open http://localhost in your browser
```

### Option 2: Local .NET

Prerequisites:
- .NET 10 SDK
- Ollama running locally with `qwen2.5:7b`, **or** an Anthropic API key

```bash
# Clone and build
git clone https://github.com/bimalghartimagar/LocalAgentTravelPlanner.git
cd LocalAgentTravelPlanner
dotnet build LocalAgentTravelPlanner.sln

# Run the Web API
dotnet run --project Api/LocalAgentTravelPlanner.Api.csproj
# Open http://localhost:5286

# Or run the console app
dotnet run --project LocalAgentTravelPlanner.csproj
```

The dev environment uses `dev-key-12345` as the API key (set in `appsettings.Development.json`). The browser UI prompts for the key on first request and stores it in `sessionStorage`.

## Configuration

| Key | Required | Source | Purpose |
|-----|----------|--------|---------|
| `ApiKey` | Yes | appsettings / env / `ApiKeyFile` | Authenticates API requests via `X-Api-Key` header |
| `ANTHROPIC_API_KEY` | No | appsettings / env | Anthropic Claude API key. Falls back to Ollama if absent |
| `OLLAMA_HOST` | No | env | Ollama endpoint (default: `http://localhost:11434`) |
| `ApiKeyFile` | No | appsettings | Path to a file containing the API key (Docker secrets) |

ASP.NET's configuration system loads keys with this precedence (last wins):

```
appsettings.json → appsettings.Development.json → environment variables
```

For deployments, mount a secret file and point `ApiKeyFile` at it, or set the env vars directly.

## API Endpoints

| Method | Route | Auth | Rate Limited | Description |
|--------|-------|------|-------------|-------------|
| `POST` | `/api/travel/plan` | API key | 5/min | Blocking — returns full plan as JSON |
| `GET` | `/api/travel/plan/stream` | API key | 5/min | SSE — streams per-agent events |
| `GET` | `/api/travel/health` | None | No | Provider availability check |

### Example request

```bash
curl -X POST http://localhost:5286/api/travel/plan \
  -H "X-Api-Key: dev-key-12345" \
  -H "Content-Type: application/json" \
  -d '{"request": "5-day trip from Tokyo to Kyoto, budget $1500"}'
```

## Architecture

### Agent pipeline

```
User Request
    ↓
Researcher  →  gathers destination data via live APIs
    ↓
Planner     →  builds day-by-day itinerary
    ↓
Accountant  →  calculates budget across 27 currencies
    ↓
Auditor     →  validates on 6 criteria using deterministic tools
    ↓
Aggregator  →  synthesizes final user-facing output
    ↓
Streamed to the browser via SSE
```

Each agent receives the full conversation history from prior agents. The Auditor scores the plan on:

- **Financial integrity** — math is correct, totals match
- **Temporal logic** — travel times realistic, no overlapping activities
- **Safety** — no restricted areas without permits, no dangerous activities
- **Groundedness** — recommendations actually appeared in research
- **Relevance** — matches the user's stated requirements
- **Completeness** — all required sections present

### Project structure

| Project | Type | Purpose |
|---------|------|---------|
| `LocalAgentTravelPlanner.csproj` | Console | Core agents, services, models, tools |
| `Api/` | ASP.NET Core | REST + SSE endpoints, middleware, static UI |
| `Tests/` | xUnit | Unit tests + MEAI evaluation tests |

### Middleware pipeline

```
Request → ForwardedHeaders → Serilog → HTTPS Redirect → ApiKeyMiddleware → RateLimiter → StaticFiles → Controllers
```

### Key technical decisions

- **`IChatClient` abstraction** — agents are provider-agnostic, swap Ollama and Anthropic via config
- **`AnthropicOptionsInjector` middleware** — `DelegatingChatClient` that injects `ModelId` and `MaxOutputTokens` per request (Anthropic SDK requires both, MAF doesn't set them)
- **Pre-pipeline intent classifier** — one cheap LLM call rejects off-topic requests before the 5-agent pipeline runs
- **MAF silent failure handling** — `ExecutorFailedEvent` and `WorkflowErrorEvent` are explicitly handled because MAF otherwise completes with empty output on agent errors
- **Empty content filtering** — `AgentRunUpdateEvent` fires with empty data during tool-calling turns; filtered before reaching the SSE stream
- **`fetch + ReadableStream` over `EventSource`** — avoids `EventSource`'s auto-reconnect restarting expensive multi-agent runs

## Testing

```bash
# Run all unit tests (130 tests)
dotnet test Tests/LocalAgentTravelPlanner.Tests.csproj

# Run a specific test class
dotnet test Tests/ --filter "FullyQualifiedName~BudgetToolsTests"

# Run LLM-as-judge evaluation tests (requires a live provider)
ENABLE_EVAL_TESTS=true dotnet test Tests/ --filter "Category=Evaluation"
```

Evaluation tests use [`Microsoft.Extensions.AI.Evaluation.Quality`](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/evaluation-overview) with metrics for Coherence, Fluency, Relevance, Truth, Completeness, and Groundedness. They are skipped by default to keep the unit-test loop fast.

## Tech Stack

- **.NET 10** with C# 13
- **Microsoft Agent Framework 1.0.0-preview** for agent orchestration
- **Microsoft.Extensions.AI** for the `IChatClient` abstraction
- **Anthropic.SDK** for Claude integration
- **OllamaSharp** for local model integration
- **ASP.NET Core** Web API with built-in rate limiting
- **Serilog** for structured logging
- **xUnit + Moq + FluentAssertions** for testing
- **nginx** as the production reverse proxy
- **Docker + docker-compose** for deployment

## Documentation

- [Building a Multi-Agent Travel Planner with Microsoft Agent Framework and .NET](https://www.endpointdev.com/blog/2026/03/building-multi-agent-travel-planner-dotnet/) — full walkthrough on the End Point blog
- [`CLAUDE.md`](CLAUDE.md) — internal guidance for Claude Code agents working on this repo
- [`AGENTS.md`](AGENTS.md) — same content for Codex

## License

This repository does not yet have an open-source license. All rights reserved until a license is added.
