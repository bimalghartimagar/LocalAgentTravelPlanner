namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Represents a single day in the travel itinerary.
    /// </summary>
    public record ItineraryDay
    {
        public required int DayNumber { get; init; }
        public required string Theme { get; init; }
        public required string Date { get; init; }

        public required TimeSlotActivity Morning { get; init; }
        public required TimeSlotActivity Afternoon { get; init; }
        public required TimeSlotActivity Evening { get; init; }

        public string? WeatherNote { get; init; }
        public string? PackingTip { get; init; }
        public decimal DailyBudget { get; init; }
    }

    /// <summary>
    /// Represents an activity in a specific time slot.
    /// </summary>
    public record TimeSlotActivity
    {
        public required string TimeRange { get; init; }   // e.g., "8 AM - 12 PM"
        public required string Activity { get; init; }
        public required string Location { get; init; }
        public string? WhyNow { get; init; }              // Weather/timing justification
        public string? TravelTip { get; init; }
        public decimal EstimatedCost { get; init; }
    }
}
