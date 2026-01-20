using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// Validation tools for the Auditor Agent.
    ///
    /// DESIGN PHILOSOPHY:
    /// These tools provide DETERMINISTIC validation that the LLM cannot do reliably:
    /// - Math calculations (LLMs are notoriously bad at arithmetic)
    /// - Time/duration parsing
    /// - Structured data extraction
    ///
    /// The LLM's job is to:
    /// 1. Decide WHICH tools to call
    /// 2. Interpret the results
    /// 3. Make judgment calls on subjective criteria
    ///
    /// WHY THIS MATTERS FOR EVALUATION (RAGAS-style):
    /// - "Faithfulness" in RAGAS checks if answers are grounded in context
    /// - Our Groundedness tool does the same: verify claims against research data
    /// - This is a hybrid approach: deterministic validation + LLM judgment
    /// </summary>
    public partial class AuditorTools
    {
        /// <summary>
        /// Validates that the sum of cost items equals the stated total.
        ///
        /// WHY THIS TOOL:
        /// LLMs struggle with arithmetic. A plan might claim "Total: 45,000 NPR"
        /// but the actual sum of items is 52,000 NPR. This tool catches that.
        ///
        /// EXAMPLE:
        /// Input: "700, 3500, 1200, 500" with statedTotal = 5900
        /// Output: PASS (sum matches)
        /// </summary>
        [Description("Validates that the sum of individual costs equals the stated total. Returns PASS/FAIL with details.")]
        public string ValidateMathConsistency(
            [Description("Comma-separated list of costs to sum")] string costs,
            [Description("The stated total to validate against")] decimal statedTotal)
        {
            var items = costs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c =>
                {
                    // Clean the string: remove currency symbols, commas in numbers
                    var cleaned = Regex.Replace(c, @"[^\d.]", "");
                    return decimal.TryParse(cleaned, out var v) ? v : 0;
                })
                .Where(v => v > 0)
                .ToList();

            if (items.Count == 0)
            {
                return """
                    ⚠️ VALIDATION ERROR: No valid cost items found.
                    Could not parse any numbers from the input.
                    """;
            }

            decimal calculatedSum = items.Sum();
            decimal difference = Math.Abs(calculatedSum - statedTotal);
            decimal tolerancePercent = 5m; // Allow 5% tolerance for rounding
            decimal tolerance = statedTotal * (tolerancePercent / 100m);

            bool isValid = difference <= tolerance;

            return $"""
                📊 MATH CONSISTENCY CHECK

                Parsed Items: {string.Join(" + ", items.Select(i => i.ToString("N0")))}
                Number of Items: {items.Count}

                Calculated Sum: {calculatedSum:N0}
                Stated Total:   {statedTotal:N0}
                Difference:     {difference:N0} ({(statedTotal != 0 ? difference / statedTotal * 100 : 0):F1}%)

                Tolerance:      {tolerance:N0} ({tolerancePercent}%)

                Result: {(isValid ? "✅ PASS - Math is consistent" : "❌ FAIL - Significant discrepancy detected")}
                {(isValid ? "" : $"\nIssue: The sum of line items ({calculatedSum:N0}) differs from stated total ({statedTotal:N0}) by {difference:N0}")}
                """;
        }

        /// <summary>
        /// Checks if the estimated cost is within the user's budget.
        ///
        /// WHY THIS TOOL:
        /// Simple comparison, but provides clear verdict and percentage calculations.
        /// Helps the Auditor make consistent budget decisions.
        /// </summary>
        [Description("Checks if an estimated cost fits within the user's budget. Returns detailed comparison.")]
        public string ValidateBudgetFit(
            [Description("The estimated total cost")] decimal estimatedCost,
            [Description("The user's stated budget")] decimal userBudget,
            [Description("Currency (NPR or USD)")] string currency = "NPR")
        {
            decimal difference = userBudget - estimatedCost;
            decimal percentUsed = userBudget != 0 ? (estimatedCost / userBudget) * 100 : 0;

            string status;
            string emoji;
            string recommendation;

            if (percentUsed <= 80)
            {
                status = "WELL WITHIN BUDGET";
                emoji = "✅";
                recommendation = "Room for upgrades or additional activities.";
            }
            else if (percentUsed <= 95)
            {
                status = "WITHIN BUDGET";
                emoji = "✅";
                recommendation = "Good budget utilization with small buffer.";
            }
            else if (percentUsed <= 105)
            {
                status = "TIGHT FIT";
                emoji = "⚠️";
                recommendation = "Very close to budget. Recommend small cost reductions.";
            }
            else if (percentUsed <= 120)
            {
                status = "OVER BUDGET";
                emoji = "❌";
                recommendation = "Exceeds budget. Cost reductions required.";
            }
            else
            {
                status = "SIGNIFICANTLY OVER BUDGET";
                emoji = "❌";
                recommendation = "Major budget revision needed or expectation adjustment.";
            }

            return $"""
                💰 BUDGET FIT CHECK

                User's Budget:    {userBudget:N0} {currency}
                Estimated Cost:   {estimatedCost:N0} {currency}

                Difference:       {(difference >= 0 ? "+" : "")}{difference:N0} {currency}
                Budget Used:      {percentUsed:F1}%

                Status: {emoji} {status}

                Recommendation: {recommendation}
                """;
        }

        /// <summary>
        /// Validates that travel times between locations are realistic.
        ///
        /// WHY THIS TOOL:
        /// A plan might schedule "Leave Pokhara at 2PM, arrive Kathmandu at 2:30PM"
        /// when the actual travel time is 6+ hours. This catches temporal impossibilities.
        ///
        /// CONNECTION TO EVALUATION:
        /// This is similar to "logical consistency" checks in evaluation frameworks.
        /// </summary>
        [Description("Validates if travel between two activities is temporally possible given the time gap.")]
        public string ValidateTravelTime(
            [Description("Origin location")] string fromLocation,
            [Description("Destination location")] string toLocation,
            [Description("Available time in minutes")] int availableMinutes,
            [Description("Mode of transport (bus, taxi, flight, walk)")] string transportMode = "taxi")
        {
            // Known travel times (in minutes) - simplified database
            // Keys are already lowercase; we normalize input before lookup
            var travelTimes = new Dictionary<(string, string, string), int>
            {
                // Kathmandu - Pokhara routes
                { ("kathmandu", "pokhara", "bus"), 420 },      // 7 hours
                { ("kathmandu", "pokhara", "taxi"), 360 },     // 6 hours
                { ("kathmandu", "pokhara", "flight"), 30 },    // 30 min flight
                { ("pokhara", "kathmandu", "bus"), 420 },
                { ("pokhara", "kathmandu", "taxi"), 360 },
                { ("pokhara", "kathmandu", "flight"), 30 },

                // Butwal - Pokhara routes
                { ("butwal", "pokhara", "bus"), 180 },         // 3 hours
                { ("butwal", "pokhara", "taxi"), 150 },        // 2.5 hours
                { ("pokhara", "butwal", "bus"), 180 },
                { ("pokhara", "butwal", "taxi"), 150 },

                // Within Pokhara
                { ("lakeside", "phewa lake", "walk"), 10 },
                { ("lakeside", "world peace pagoda", "taxi"), 30 },
                { ("lakeside", "world peace pagoda", "walk"), 90 },
                { ("lakeside", "sarangkot", "taxi"), 45 },
                { ("lakeside", "davis falls", "taxi"), 20 },
                { ("pokhara airport", "lakeside", "taxi"), 20 },

                // Within Kathmandu
                { ("thamel", "pashupatinath", "taxi"), 30 },
                { ("thamel", "swayambhunath", "taxi"), 20 },
                { ("thamel", "boudhanath", "taxi"), 35 },
                { ("kathmandu airport", "thamel", "taxi"), 30 },
            };

            var fromNorm = fromLocation.ToLower().Trim();
            var toNorm = toLocation.ToLower().Trim();
            var modeNorm = transportMode.ToLower().Trim();

            // Try to find travel time
            int? estimatedMinutes = null;

            if (travelTimes.TryGetValue((fromNorm, toNorm, modeNorm), out var exactTime))
            {
                estimatedMinutes = exactTime;
            }
            else
            {
                // Try to estimate based on mode
                estimatedMinutes = modeNorm switch
                {
                    "walk" => 60,      // Default 1 hour walk
                    "taxi" => 30,      // Default 30 min taxi
                    "bus" => 60,       // Default 1 hour bus
                    "flight" => 45,    // Default 45 min including airport time
                    _ => 45
                };
            }

            bool isPossible = availableMinutes >= estimatedMinutes;
            int buffer = availableMinutes - estimatedMinutes.Value;

            return $"""
                🕐 TRAVEL TIME VALIDATION

                Route: {fromLocation} → {toLocation}
                Transport Mode: {transportMode}

                Estimated Travel Time: {estimatedMinutes} minutes ({estimatedMinutes / 60.0:F1} hours)
                Available Time Window: {availableMinutes} minutes ({availableMinutes / 60.0:F1} hours)

                Buffer: {(buffer >= 0 ? "+" : "")}{buffer} minutes

                Result: {(isPossible ? "✅ POSSIBLE - Adequate time for travel" : "❌ IMPOSSIBLE - Insufficient time for travel")}
                {(isPossible ? "" : $"\nIssue: Need at least {estimatedMinutes} minutes, but only {availableMinutes} available")}
                """;
        }

        /// <summary>
        /// Checks if a location or attraction was mentioned in the research context.
        ///
        /// WHY THIS TOOL:
        /// This is the "Groundedness" or "Faithfulness" check - critical for evaluation!
        /// If the Planner mentions "Hotel Paradise" but the Researcher never found it,
        /// that's a hallucination and should be flagged.
        ///
        /// RAGAS CONNECTION:
        /// - RAGAS "Faithfulness" metric checks if claims are supported by context
        /// - This tool does exactly that for hotels and attractions
        /// </summary>
        [Description("Checks if a hotel or attraction name appears in the research context. Used to verify claims are grounded in research.")]
        public string CheckGroundedness(
            [Description("Name of the hotel or attraction to verify")] string itemName,
            [Description("The research context text to search in")] string researchContext)
        {
            if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(researchContext))
            {
                return "⚠️ VALIDATION ERROR: Item name and research context are required.";
            }

            // Normalize for comparison
            var itemNormalized = itemName.ToLower().Trim();
            var contextNormalized = researchContext.ToLower();

            // Check for exact match
            bool exactMatch = contextNormalized.Contains(itemNormalized);

            // Check for partial matches (individual significant words)
            var significantWords = itemNormalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3) // Skip short words like "the", "and"
                .ToList();

            int matchedWords = significantWords.Count(w => contextNormalized.Contains(w));
            double wordMatchPercent = significantWords.Count > 0
                ? (double)matchedWords / significantWords.Count * 100
                : 0;

            string status;
            string confidence;

            if (exactMatch)
            {
                status = "✅ VERIFIED";
                confidence = "HIGH";
            }
            else if (wordMatchPercent >= 75)
            {
                status = "⚠️ PARTIAL MATCH";
                confidence = "MEDIUM";
            }
            else if (wordMatchPercent >= 50)
            {
                status = "⚠️ WEAK MATCH";
                confidence = "LOW";
            }
            else
            {
                status = "❌ NOT FOUND";
                confidence = "NONE";
            }

            return $"""
                🔍 GROUNDEDNESS CHECK

                Looking for: "{itemName}"

                Exact Match: {(exactMatch ? "Yes" : "No")}
                Word Matches: {matchedWords}/{significantWords.Count} significant words ({wordMatchPercent:F0}%)

                Status: {status}
                Confidence: {confidence}

                {(exactMatch ? $"Evidence: The item \"{itemName}\" appears in the research context." : "")}
                {(!exactMatch && wordMatchPercent < 50 ? $"Warning: \"{itemName}\" may be hallucinated - not found in research data." : "")}
                """;
        }

        /// <summary>
        /// Checks if a location has safety concerns or requires permits.
        ///
        /// WHY THIS TOOL:
        /// Some destinations require special permits (e.g., Upper Mustang in Nepal).
        /// Some activities might be dangerous in certain seasons.
        /// The Auditor should flag these.
        /// </summary>
        [Description("Checks safety concerns and permit requirements for a location.")]
        public string CheckSafetyRequirements(
            [Description("Location to check")] string location)
        {
            var locationLower = location.ToLower().Trim();

            // Locations requiring permits
            var permitRequired = new Dictionary<string, string>
            {
                { "upper mustang", "Special Restricted Area Permit ($500) + ACAP permit required" },
                { "dolpo", "Special Restricted Area Permit ($500) required" },
                { "manaslu", "Restricted Area Permit ($70-100) + MCAP permit required" },
                { "kanchenjunga", "Restricted Area Permit required" },
                { "upper dolpa", "Special permit ($500/10 days) required" }
            };

            // Locations with safety concerns
            var safetyConcerns = new Dictionary<string, string>
            {
                { "everest", "Altitude sickness risk above 3000m. Acclimatization required." },
                { "annapurna", "Altitude concerns on high passes. Weather can change rapidly." },
                { "langtang", "Earthquake damage in some areas. Check current trail conditions." },
                { "chitwan", "Wildlife encounters possible. Follow guide instructions strictly." }
            };

            // Seasonal concerns
            var seasonalWarnings = new Dictionary<string, string>
            {
                { "monsoon", "June-September: Heavy rains, landslides, leeches on trails" },
                { "winter", "December-February: Snow at high altitude, some passes closed" }
            };

            var issues = new List<string>();
            var permits = new List<string>();

            foreach (var kvp in permitRequired)
            {
                if (locationLower.Contains(kvp.Key))
                {
                    permits.Add(kvp.Value);
                }
            }

            foreach (var kvp in safetyConcerns)
            {
                if (locationLower.Contains(kvp.Key))
                {
                    issues.Add(kvp.Value);
                }
            }

            bool hasIssues = permits.Count > 0 || issues.Count > 0;

            return $"""
                ⚠️ SAFETY & COMPLIANCE CHECK

                Location: {location}

                Permit Requirements:
                {(permits.Count > 0 ? string.Join("\n", permits.Select(p => $"  • {p}")) : "  • None required for standard tourism")}

                Safety Concerns:
                {(issues.Count > 0 ? string.Join("\n", issues.Select(i => $"  • {i}")) : "  • No major safety concerns")}

                Status: {(hasIssues ? "⚠️ ATTENTION REQUIRED" : "✅ CLEAR")}

                {(hasIssues ? "Note: The plan should acknowledge these requirements/concerns." : "")}
                """;
        }

        /// <summary>
        /// Formats the final audit decision based on criteria scores.
        ///
        /// WHY THIS TOOL:
        /// Ensures consistent decision logic across all audits.
        /// The LLM provides the scores; this tool applies the decision rules.
        ///
        /// DECISION RULES:
        /// - APPROVED: All scores >= 3
        /// - FLAGGED: Any score = 2, no score = 1
        /// - REJECTED: Any score = 1
        /// - IMPOSSIBLE: Average score < 2
        /// </summary>
        [Description("Determines the final audit decision based on the four criteria scores (1-5 each).")]
        public string DetermineAuditDecision(
            [Description("Financial Integrity score (1-5)")] int financialScore,
            [Description("Temporal Logic score (1-5)")] int temporalScore,
            [Description("Safety Compliance score (1-5)")] int safetyScore,
            [Description("Groundedness score (1-5)")] int groundednessScore)
        {
            var scores = new[] { financialScore, temporalScore, safetyScore, groundednessScore };
            var scoreNames = new[] { "Financial Integrity", "Temporal Logic", "Safety Compliance", "Groundedness" };

            // Validate scores
            if (scores.Any(s => s < 1 || s > 5))
            {
                return "⚠️ ERROR: All scores must be between 1 and 5.";
            }

            double average = scores.Average();
            int minScore = scores.Min();
            int maxScore = scores.Max();
            int countLow = scores.Count(s => s <= 2);
            int countCritical = scores.Count(s => s == 1);

            string decision;
            string emoji;
            string explanation;

            if (countCritical > 0)
            {
                decision = "REJECTED";
                emoji = "❌";
                var criticalCriteria = scores
                    .Select((s, i) => (Score: s, Name: scoreNames[i]))
                    .Where(x => x.Score == 1)
                    .Select(x => x.Name);
                explanation = $"Critical failure in: {string.Join(", ", criticalCriteria)}";
            }
            else if (average < 2)
            {
                decision = "IMPOSSIBLE";
                emoji = "🚫";
                explanation = "Overall quality too low. Plan is fundamentally flawed.";
            }
            else if (countLow > 0)
            {
                decision = "FLAGGED";
                emoji = "⚠️";
                var lowCriteria = scores
                    .Select((s, i) => (Score: s, Name: scoreNames[i]))
                    .Where(x => x.Score == 2)
                    .Select(x => x.Name);
                explanation = $"Issues in: {string.Join(", ", lowCriteria)}. Needs attention.";
            }
            else
            {
                decision = "APPROVED";
                emoji = "✅";
                explanation = "All criteria meet acceptable standards.";
            }

            return $"""
                🏆 AUDIT DECISION

                Scores:
                  • Financial Integrity: {financialScore}/5 {"⭐".PadRight(financialScore, '⭐')}
                  • Temporal Logic:      {temporalScore}/5 {"⭐".PadRight(temporalScore, '⭐')}
                  • Safety Compliance:   {safetyScore}/5 {"⭐".PadRight(safetyScore, '⭐')}
                  • Groundedness:        {groundednessScore}/5 {"⭐".PadRight(groundednessScore, '⭐')}

                Statistics:
                  • Average Score: {average:F1}/5
                  • Lowest Score:  {minScore}/5
                  • Highest Score: {maxScore}/5

                ═══════════════════════════════════════
                DECISION: {emoji} {decision}
                ═══════════════════════════════════════

                {explanation}
                """;
        }
    }
}
