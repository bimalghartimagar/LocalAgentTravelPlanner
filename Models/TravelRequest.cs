namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Represents the user's travel request input.
    /// </summary>
    public record TravelRequest
    {
        public required string Origin { get; init; }
        public required string Destination { get; init; }
        public required int DurationDays { get; init; }
        public required decimal Budget { get; init; }
        public required string Currency { get; init; }  // NPR or USD
        public string? TravelStyle { get; init; }       // Frugal, Medium, Luxury
        public string? TravelerType { get; init; }      // Family, Solo, Couple
        public List<string> Interests { get; init; } = [];
    }
}
