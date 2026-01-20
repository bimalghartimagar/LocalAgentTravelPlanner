namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Complete travel plan aggregating all agent outputs.
    /// </summary>
    public record TravelPlan
    {
        // Trip basics
        public required TravelRequest Request { get; init; }

        // Research data
        public required ResearchContext Research { get; init; }

        // Itinerary
        public required List<ItineraryDay> Itinerary { get; init; }

        // Budget
        public required FullBudgetAnalysis BudgetAnalysis { get; init; }
        public required BudgetBreakdown SelectedBudget { get; init; }

        // Totals
        public decimal TotalEstimatedCost { get; init; }
        public bool IsWithinBudget => TotalEstimatedCost <= Request.Budget;

        // Metadata
        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    }
}
