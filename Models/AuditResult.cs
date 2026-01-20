namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Represents the complete audit result from the Auditor Agent.
    /// This is the structured output that captures the evaluation of a travel plan.
    ///
    /// WHY THIS DESIGN:
    /// - Uses C# record for immutability (audit results shouldn't change after creation)
    /// - Each criterion has both a Score (1-5) and Reasoning (explainability)
    /// - Decision is an enum to enforce valid values (APPROVED/FLAGGED/REJECTED/IMPOSSIBLE)
    /// - This structure enables downstream systems to programmatically act on audit results
    /// </summary>
    public record AuditResult
    {
        /// <summary>
        /// The final decision: APPROVED, FLAGGED, REJECTED, or IMPOSSIBLE
        /// </summary>
        public required AuditDecision Decision { get; init; }

        /// <summary>
        /// Overall confidence score (1-5) computed from individual criteria
        /// </summary>
        public required int OverallScore { get; init; }

        /// <summary>
        /// Human-readable summary of the audit findings
        /// </summary>
        public required string Summary { get; init; }

        /// <summary>
        /// Individual scores and reasoning for each evaluation criterion
        /// </summary>
        public required AuditCriteria Criteria { get; init; }

        /// <summary>
        /// List of specific issues found (empty if plan is clean)
        /// </summary>
        public required List<string> Issues { get; init; }

        /// <summary>
        /// Suggestions for improving the plan (optional)
        /// </summary>
        public List<string>? Suggestions { get; init; }

        /// <summary>
        /// When the audit was performed
        /// </summary>
        public DateTime AuditedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The four evaluation criteria the Auditor uses to score a travel plan.
    ///
    /// WHY THESE FOUR:
    /// 1. Financial Integrity - Can't have a plan that doesn't add up mathematically
    /// 2. Temporal Logic - Can't teleport between cities or be in two places at once
    /// 3. Safety Compliance - Must respect laws, permits, and traveler safety
    /// 4. Groundedness - Must only reference verified data (anti-hallucination)
    ///
    /// SCORING SCALE (1-5):
    /// 5 = Perfect, no issues
    /// 4 = Minor issues, acceptable
    /// 3 = Some issues, needs attention
    /// 2 = Significant issues, problematic
    /// 1 = Critical failure, unacceptable
    /// </summary>
    public record AuditCriteria
    {
        /// <summary>
        /// Does the math add up? Sum of items = total, total <= budget
        /// </summary>
        public required CriterionScore FinancialIntegrity { get; init; }

        /// <summary>
        /// Is the schedule physically possible? Realistic travel times, no overlaps
        /// </summary>
        public required CriterionScore TemporalLogic { get; init; }

        /// <summary>
        /// Are there safety issues? Restricted zones, dangerous activities, missing permits
        /// </summary>
        public required CriterionScore SafetyCompliance { get; init; }

        /// <summary>
        /// Is everything based on verified research? No hallucinated hotels/attractions
        /// </summary>
        public required CriterionScore Groundedness { get; init; }

        /// <summary>
        /// Calculates the average score across all criteria (for quick assessment)
        /// </summary>
        public double AverageScore =>
            (FinancialIntegrity.Score + TemporalLogic.Score +
             SafetyCompliance.Score + Groundedness.Score) / 4.0;
    }

    /// <summary>
    /// A single criterion's evaluation with score and explanation.
    ///
    /// WHY INCLUDE REASONING:
    /// - Explainability is crucial for trust in AI systems
    /// - Users need to understand WHY something was flagged
    /// - Enables debugging when the LLM makes mistakes
    /// - Aligns with RAGAS principle of providing evidence for scores
    /// </summary>
    public record CriterionScore
    {
        /// <summary>
        /// Numeric score from 1 (critical failure) to 5 (perfect)
        /// </summary>
        public required int Score { get; init; }

        /// <summary>
        /// Human-readable explanation of why this score was given
        /// </summary>
        public required string Reasoning { get; init; }

        /// <summary>
        /// Specific evidence/examples that support the score
        /// </summary>
        public List<string>? Evidence { get; init; }
    }

    /// <summary>
    /// The four possible audit decisions.
    ///
    /// DECISION LOGIC:
    /// - APPROVED: All criteria >= 3, no critical issues → Safe to show user
    /// - FLAGGED: One criterion = 2, none = 1 → Show with warnings
    /// - REJECTED: Any criterion = 1 → Do not show, needs revision
    /// - IMPOSSIBLE: Fundamentally unrealistic (e.g., luxury trip for $10) → Cannot proceed
    /// </summary>
    public enum AuditDecision
    {
        /// <summary>Plan passes all checks, ready for user</summary>
        Approved,

        /// <summary>Plan has minor issues, show with warnings</summary>
        Flagged,

        /// <summary>Plan has critical issues, needs revision</summary>
        Rejected,

        /// <summary>Plan is fundamentally impossible (budget vs expectations mismatch)</summary>
        Impossible
    }
}
