using System.Diagnostics;

namespace LocalAgentTravelPlanner.Services;

/// <summary>
/// Central OpenTelemetry <see cref="ActivitySource"/> for the core service. Consumers
/// (typically the Api project) register this source name with the tracer provider so
/// per-turn and per-phase spans light up in whichever OTLP-compatible backend they
/// wire up (Jaeger, Tempo, Honeycomb, Datadog, etc.).
///
/// Name/version double as the OpenTelemetry <c>service.name</c> attribute contribution
/// when <see cref="ActivitySource.Name"/> is registered on the tracer provider.
/// </summary>
public static class Diagnostics
{
    public const string ActivitySourceName = "LocalAgentTravelPlanner";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
}
