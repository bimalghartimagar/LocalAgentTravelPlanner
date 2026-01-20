namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Complete budget breakdown from the Accountant Agent.
    /// </summary>
    public record BudgetBreakdown
    {
        public required string TierName { get; init; }       // Frugal, Medium, High-End
        public required decimal DailyEstimate { get; init; }
        public required decimal TotalEstimate { get; init; }
        public required string KeyStrategy { get; init; }

        public required CategoryCost Accommodation { get; init; }
        public required CategoryCost Food { get; init; }
        public required CategoryCost Transport { get; init; }
        public required CategoryCost Activities { get; init; }
        public CategoryCost? Miscellaneous { get; init; }

        public decimal GrandTotal => Accommodation.Total + Food.Total +
                                     Transport.Total + Activities.Total +
                                     (Miscellaneous?.Total ?? 0);
    }

    /// <summary>
    /// Cost breakdown for a single category.
    /// </summary>
    public record CategoryCost(
        string Category,
        decimal DailyAmount,
        decimal Total,
        string Notes
    );

    /// <summary>
    /// Full budget analysis across all tiers.
    /// </summary>
    public record FullBudgetAnalysis
    {
        public required BudgetBreakdown Frugal { get; init; }
        public required BudgetBreakdown Medium { get; init; }
        public required BudgetBreakdown HighEnd { get; init; }
        public required string RecommendedTier { get; init; }
        public required string Reasoning { get; init; }
    }
}
