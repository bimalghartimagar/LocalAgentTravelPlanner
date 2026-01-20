namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Research data gathered by the Researcher Agent.
    /// </summary>
    public record ResearchContext
    {
        public required string Destination { get; init; }

        // Weather info
        public required string WeatherSummary { get; init; }
        public double? TemperatureCelsius { get; init; }
        public string? WeatherAlerts { get; init; }

        // Transportation
        public required string TransportOptions { get; init; }
        public decimal? FlightEstimate { get; init; }
        public decimal? BusEstimate { get; init; }
        public string? TravelDuration { get; init; }

        // Verified locations
        public List<VerifiedHotel> Hotels { get; init; } = [];
        public List<VerifiedAttraction> Attractions { get; init; } = [];
        public List<string> Restaurants { get; init; } = [];

        // Local info
        public string? LocalTips { get; init; }
        public string? SafetyNotes { get; init; }
    }

    /// <summary>
    /// A hotel verified by the research agent.
    /// </summary>
    public record VerifiedHotel(
        string Name,
        string Location,
        decimal PricePerNight,
        string Category  // Budget, Mid-range, Luxury
    );

    /// <summary>
    /// An attraction verified by the research agent.
    /// </summary>
    public record VerifiedAttraction(
        string Name,
        string Description,
        decimal? EntranceFee,
        string? RecommendedDuration
    );
}
