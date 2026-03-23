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
        /// LLMs struggle with arithmetic. A plan might claim "Total: $4,500"
        /// but the actual sum of items is $5,200. This tool catches that.
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
            [Description("Currency code (e.g., USD, EUR, GBP)")] string currency = "USD")
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
        /// A plan might schedule a transfer that is temporally impossible given the
        /// available time window. This catches such inconsistencies using reasonable
        /// default estimates per transport mode.
        ///
        /// CONNECTION TO EVALUATION:
        /// This is similar to "logical consistency" checks in evaluation frameworks.
        /// </summary>
        [Description("Validates if travel between two activities is temporally possible given the time gap.")]
        public string ValidateTravelTime(
            [Description("Origin location")] string fromLocation,
            [Description("Destination location")] string toLocation,
            [Description("Available time in minutes")] int availableMinutes,
            [Description("Mode of transport (bus, taxi, flight, walk)")] string transportMode = "taxi",
            [Description("Estimated travel time in minutes if known (0 to use default)")] int estimatedTravelMinutes = 0)
        {
            var modeNorm = transportMode.ToLower().Trim();

            // Use caller-provided estimate if available, otherwise apply mode-based defaults.
            // These defaults represent typical intra-city or short-distance transfers;
            // for intercity routes the LLM should provide an estimate from the research data.
            int estimatedMinutes = estimatedTravelMinutes > 0
                ? estimatedTravelMinutes
                : modeNorm switch
                {
                    "walk" or "walking" => 60,
                    "taxi" or "car" or "drive" or "rideshare" => 30,
                    "bus" or "coach" => 60,
                    "train" or "rail" or "metro" or "subway" => 45,
                    "flight" or "plane" => 120,  // includes airport overhead
                    "ferry" or "boat" => 90,
                    _ => 45
                };

            bool isPossible = availableMinutes >= estimatedMinutes;
            int buffer = availableMinutes - estimatedMinutes;

            return $"""
                TRAVEL TIME VALIDATION

                Route: {fromLocation} → {toLocation}
                Transport Mode: {transportMode}

                Estimated Travel Time: {estimatedMinutes} minutes ({estimatedMinutes / 60.0:F1} hours)
                Available Time Window: {availableMinutes} minutes ({availableMinutes / 60.0:F1} hours)

                Buffer: {(buffer >= 0 ? "+" : "")}{buffer} minutes

                Result: {(isPossible ? "POSSIBLE - Adequate time for travel" : "IMPOSSIBLE - Insufficient time for travel")}
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
        /// Checks if a location or activity has safety concerns or requires permits.
        ///
        /// WHY THIS TOOL:
        /// Some destinations require permits, visas, or special clearances.
        /// Some activities carry inherent risks (altitude, wildlife, extreme sports).
        /// The Auditor should flag these so the plan acknowledges them.
        /// </summary>
        [Description("Checks safety concerns and permit requirements for a location or activity.")]
        public string CheckSafetyRequirements(
            [Description("Location or activity to check")] string location)
        {
            var locationLower = location.ToLower().Trim();

            // Activities/locations that commonly require permits or advance booking
            var permitRequired = new Dictionary<string, string>
            {
                // Trekking permits
                { "upper mustang", "Nepal: Special Restricted Area Permit ($500) + ACAP permit required" },
                { "manaslu", "Nepal: Restricted Area Permit ($70-100) + MCAP permit required" },
                { "inca trail", "Peru: Advance permit required (sells out months ahead, ~$250)" },
                { "mount kilimanjaro", "Tanzania: Park entry fees ($70-100/day) + licensed guide mandatory" },
                { "torres del paine", "Chile: Advance campsite/refugio reservation required in peak season" },
                { "galapagos", "Ecuador: National park entry fee ($100) + guided tour mandatory" },
                // Restricted/controlled areas
                { "north korea", "Guided tour through authorized agency mandatory" },
                { "bhutan", "Minimum daily tariff ($200-250/day) + licensed tour operator required" },
                { "chernobyl", "Ukraine: Authorized guided tour only" },
                { "mount athos", "Greece: Special permit (diamonitirion) required; limited daily visitors" },
                { "sentinel island", "India: Strictly prohibited — illegal to visit" },
            };

            // Activities/environments with inherent safety concerns
            var safetyConcerns = new Dictionary<string, string>
            {
                // Altitude
                { "everest", "Altitude sickness risk above 3000m. Acclimatization schedule required." },
                { "annapurna", "High-altitude passes. Weather can change rapidly." },
                { "kilimanjaro", "Altitude sickness risk. Acclimatization days recommended." },
                { "machu picchu", "Moderate altitude (2430m). Some visitors experience mild symptoms." },
                { "la paz", "High altitude city (3640m). Take it easy on arrival day." },
                { "lhasa", "High altitude (3650m). Acclimatization required before excursions." },
                // Wildlife/nature
                { "safari", "Wildlife encounters. Follow guide instructions strictly. Stay in vehicle." },
                { "scuba", "Decompression sickness risk. Ensure PADI/SSI certification. No flying within 24h." },
                { "bungee", "Extreme sport. Verify operator certifications and safety record." },
                { "paragliding", "Weather-dependent. Use licensed operators only." },
                { "white water", "Rafting risk levels vary (Class I–V). Match to experience level." },
                // Regional
                { "amazon", "Tropical disease risk. Malaria prophylaxis may be required." },
                { "sahara", "Extreme heat and dehydration risk. Travel with experienced guide." },
                { "arctic", "Extreme cold, polar bear risk. Specialized gear and guide required." },
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

        /// <summary>
        /// Checks if the plan addresses what the user actually asked for.
        ///
        /// WHY THIS TOOL:
        /// The plan might be well-written but completely miss the user's intent.
        /// e.g., User asks for "family trip" but plan has solo adventure activities.
        /// e.g., User asks for "Kyoto" but plan covers Tokyo instead.
        ///
        /// RAGAS CONNECTION:
        /// This maps to the "Answer Relevancy" metric - does the output actually
        /// address the user's question/request?
        /// </summary>
        [Description("Checks if the travel plan addresses the user's original request by extracting and matching key requirements.")]
        public string CheckRelevance(
            [Description("The user's original travel request")] string userRequest,
            [Description("The generated travel plan text")] string planText)
        {
            if (string.IsNullOrWhiteSpace(userRequest) || string.IsNullOrWhiteSpace(planText))
            {
                return "⚠️ VALIDATION ERROR: Both user request and plan text are required.";
            }

            var requestLower = userRequest.ToLower();
            var planLower = planText.ToLower();

            var checks = new List<(string Requirement, bool Found, string Detail)>();

            // Extract destination names from the request dynamically.
            // Matches "to <Place>", "from <Place>", "in <Place>", or "visit <Place>" patterns
            // where <Place> is one or more capitalized words (e.g., "Kyoto", "Ho Chi Minh City").
            var destinationPatterns = new[]
            {
                @"(?:to|from|in|visit|visiting)\s+((?:[A-Z][a-zA-Z]+(?:\s+[A-Z][a-zA-Z]+)*))",
                @"trip\s+(?:to|from|in)\s+((?:[A-Z][a-zA-Z]+(?:\s+[A-Z][a-zA-Z]+)*))"
            };

            var extractedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in destinationPatterns)
            {
                foreach (Match m in Regex.Matches(userRequest, pattern))
                {
                    var dest = m.Groups[1].Value.Trim();
                    // Skip common false positives
                    if (!string.Equals(dest, "Day", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(dest, "Budget", StringComparison.OrdinalIgnoreCase))
                    {
                        extractedDestinations.Add(dest);
                    }
                }
            }

            foreach (var dest in extractedDestinations)
            {
                var destLower = dest.ToLower();
                checks.Add(("Destination: " + dest,
                    planLower.Contains(destLower),
                    planLower.Contains(destLower) ? "Destination addressed in plan" : "Requested destination NOT found in plan"));
            }

            // Check duration mentioned
            var durationMatch = Regex.Match(requestLower, @"(\d+)\s*[-]?\s*day");
            if (durationMatch.Success)
            {
                var requestedDays = int.Parse(durationMatch.Groups[1].Value);
                var planDayMatches = Regex.Matches(planLower, @"day\s*(\d+)");
                int planDays = planDayMatches.Count > 0
                    ? planDayMatches.Cast<Match>().Max(m => int.Parse(m.Groups[1].Value))
                    : 0;

                // Also check "X days" pattern in plan
                var planDurationMatch = Regex.Match(planLower, @"(\d+)\s*days?");
                if (planDurationMatch.Success)
                {
                    int mentioned = int.Parse(planDurationMatch.Groups[1].Value);
                    if (mentioned > planDays) planDays = mentioned;
                }

                bool daysMatch = planDays >= requestedDays;
                checks.Add(($"Duration: {requestedDays} days",
                    daysMatch,
                    daysMatch ? $"Plan covers {planDays} days (requested {requestedDays})" : $"Plan only covers {planDays} days but {requestedDays} requested"));
            }

            // Check budget mentioned
            var budgetMatch = Regex.Match(requestLower, @"budget\s*(?:of\s+)?(?:[$€£¥₹]?\s*)?(\d[\d,]*)\s*(?:npr|usd|eur|gbp|jpy|inr|[$€£¥₹])?", RegexOptions.IgnoreCase);
            if (budgetMatch.Success)
            {
                var budgetStr = budgetMatch.Groups[1].Value.Replace(",", "");
                bool budgetAddressed = planLower.Contains("budget") || planLower.Contains("cost") || planLower.Contains("total");
                checks.Add(($"Budget: {budgetStr}",
                    budgetAddressed,
                    budgetAddressed ? "Budget/cost analysis present in plan" : "No budget analysis found in plan"));
            }

            // Check travel style keywords
            var styleKeywords = new Dictionary<string, string[]>
            {
                { "family", new[] { "family", "kid", "child", "children" } },
                { "solo", new[] { "solo", "alone", "independent" } },
                { "luxury", new[] { "luxury", "premium", "5-star", "five star", "high-end" } },
                { "budget", new[] { "budget", "cheap", "affordable", "frugal", "backpack" } },
                { "adventure", new[] { "adventure", "trek", "hike", "rafting", "bungee" } },
                { "honeymoon", new[] { "honeymoon", "romantic", "couple" } }
            };

            foreach (var style in styleKeywords)
            {
                if (requestLower.Contains(style.Key))
                {
                    bool styleAddressed = style.Value.Any(kw => planLower.Contains(kw));
                    checks.Add(($"Travel style: {style.Key}",
                        styleAddressed,
                        styleAddressed ? $"Plan addresses '{style.Key}' travel style" : $"Plan does NOT address requested '{style.Key}' style"));
                }
            }

            // Calculate score
            int totalChecks = checks.Count;
            int passedChecks = checks.Count(c => c.Found);
            double relevancePercent = totalChecks > 0 ? (double)passedChecks / totalChecks * 100 : 0;

            string status;
            if (relevancePercent >= 90) status = "✅ HIGHLY RELEVANT";
            else if (relevancePercent >= 70) status = "✅ RELEVANT";
            else if (relevancePercent >= 50) status = "⚠️ PARTIALLY RELEVANT";
            else status = "❌ NOT RELEVANT";

            int score = relevancePercent switch
            {
                >= 90 => 5,
                >= 75 => 4,
                >= 60 => 3,
                >= 40 => 2,
                _ => 1
            };

            return $"""
                🎯 RELEVANCE CHECK

                User Request: "{userRequest}"

                Requirements Found & Matched:
                {string.Join("\n", checks.Select(c => $"  {(c.Found ? "✅" : "❌")} {c.Requirement} - {c.Detail}"))}

                Score: {passedChecks}/{totalChecks} requirements addressed ({relevancePercent:F0}%)
                Suggested Score: {score}/5

                Status: {status}
                """;
        }

        /// <summary>
        /// Checks if the travel plan covers all expected sections.
        ///
        /// WHY THIS TOOL:
        /// A plan might answer the right question but be missing key sections.
        /// e.g., Has itinerary but no budget. Has budget but no accommodation details.
        ///
        /// RAGAS CONNECTION:
        /// This maps to "Context Recall" / completeness - does the output cover
        /// all aspects that a good travel plan should have?
        /// </summary>
        [Description("Checks if the travel plan contains all expected sections (itinerary, budget, accommodation, transport, safety).")]
        public string CheckCompleteness(
            [Description("The generated travel plan text")] string planText,
            [Description("Number of days in the trip")] int expectedDays)
        {
            if (string.IsNullOrWhiteSpace(planText))
            {
                return "⚠️ VALIDATION ERROR: Plan text is required.";
            }

            var planLower = planText.ToLower();

            var sections = new List<(string Section, string[] Keywords, bool Required, bool Found)>();

            // Required sections
            sections.Add(("Itinerary/Daily Plan",
                new[] { "day 1", "day 2", "itinerary", "morning", "afternoon", "evening", "schedule" },
                true, false));

            sections.Add(("Budget/Cost Breakdown",
                new[] { "budget", "cost", "total", "expense", "price", "usd", "eur", "$", "€", "£" },
                true, false));

            sections.Add(("Accommodation",
                new[] { "hotel", "hostel", "guesthouse", "resort", "accommodation", "stay", "lodge" },
                true, false));

            sections.Add(("Transportation",
                new[] { "bus", "flight", "taxi", "transport", "travel", "drive", "walk" },
                true, false));

            // Optional but recommended sections
            sections.Add(("Food & Dining",
                new[] { "food", "restaurant", "meal", "breakfast", "lunch", "dinner", "dining", "cafe" },
                false, false));

            sections.Add(("Attractions/Activities",
                new[] { "visit", "attraction", "activity", "museum", "temple", "lake", "trek", "tour" },
                false, false));

            sections.Add(("Safety Information",
                new[] { "safety", "emergency", "police", "hospital", "precaution", "warning", "permit" },
                false, false));

            sections.Add(("Weather/Best Time",
                new[] { "weather", "temperature", "rain", "season", "climate", "forecast" },
                false, false));

            // Check each section
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                bool found = s.Keywords.Any(kw => planLower.Contains(kw));
                sections[i] = (s.Section, s.Keywords, s.Required, found);
            }

            // Check day coverage
            var daysCovered = new List<int>();
            for (int d = 1; d <= expectedDays; d++)
            {
                if (planLower.Contains($"day {d}") || planLower.Contains($"day{d}"))
                {
                    daysCovered.Add(d);
                }
            }

            bool allDaysCovered = daysCovered.Count >= expectedDays;

            int requiredCount = sections.Count(s => s.Required);
            int requiredFound = sections.Count(s => s.Required && s.Found);
            int optionalCount = sections.Count(s => !s.Required);
            int optionalFound = sections.Count(s => !s.Required && s.Found);

            int totalFound = requiredFound + optionalFound;
            int totalSections = sections.Count;
            double completenessPercent = (double)totalFound / totalSections * 100;

            // Factor in day coverage
            if (!allDaysCovered)
            {
                completenessPercent *= 0.8; // 20% penalty for missing days
            }

            int score = completenessPercent switch
            {
                >= 85 => 5,
                >= 70 => 4,
                >= 55 => 3,
                >= 40 => 2,
                _ => 1
            };

            string status;
            if (completenessPercent >= 85) status = "✅ COMPREHENSIVE";
            else if (completenessPercent >= 70) status = "✅ ADEQUATE";
            else if (completenessPercent >= 55) status = "⚠️ PARTIAL";
            else status = "❌ INCOMPLETE";

            return $"""
                📋 COMPLETENESS CHECK

                Required Sections:
                {string.Join("\n", sections.Where(s => s.Required).Select(s => $"  {(s.Found ? "✅" : "❌")} {s.Section}"))}

                Optional Sections:
                {string.Join("\n", sections.Where(s => !s.Required).Select(s => $"  {(s.Found ? "✅" : "❌")} {s.Section}"))}

                Day Coverage:
                  Expected: {expectedDays} days
                  Found: {daysCovered.Count} days ({string.Join(", ", daysCovered.Select(d => $"Day {d}"))})
                  {(allDaysCovered ? "✅ All days covered" : $"❌ Missing {expectedDays - daysCovered.Count} day(s)")}

                Summary:
                  Required: {requiredFound}/{requiredCount}
                  Optional: {optionalFound}/{optionalCount}
                  Total: {totalFound}/{totalSections} ({completenessPercent:F0}%)
                  Suggested Score: {score}/5

                Status: {status}
                """;
        }
    }
}
