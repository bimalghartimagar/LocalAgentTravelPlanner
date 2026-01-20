# Auditor Agent Implementation Plan

> **Objective:** Implement a production-grade Auditor Agent for the Multi-Agent Travel Planner system using Microsoft Agent Framework (MAF) in C# .NET 10.

---

## 1. Overview

The **Auditor Agent** is the _critical validation node_ in the travel planning workflow. It operates as the final gatekeeper, evaluating the combined output from all preceding agents (Researcher, Planner, Accountant/Budget Calculator) before delivering the itinerary to the user.

### Key Responsibilities

- **Validate** the compiled travel plan against strict evaluation criteria
- **Score** the itinerary on a 1-5 scale across multiple metrics
- **Flag** impossible, unsafe, or inconsistent plans
- **Provide** detailed reasoning for each audit decision

---

## 2. Technical Architecture

### 2.1 File Structure

```
LocalAgentTravelPlanner/
├── Agents/
│   └── AuditorAgent.cs           # Main agent implementation
├── Models/
│   ├── AuditResult.cs            # Audit scoring model
│   ├── AuditCriteria.cs          # Evaluation criteria definitions
│   ├── ItineraryItem.cs          # Individual itinerary line item
│   └── TravelPlan.cs             # Updated with full structure
├── Tools/
│   └── AuditorTools.cs           # Validation helper functions
└── Program.cs                    # Updated workflow integration
```

### 2.2 Dependencies

- `Microsoft.Agents.AI`
- `Microsoft.Agents.AI.Abstractions`
- `Microsoft.Agents.AI.Workflows`
- `Microsoft.Extensions.AI`
- `OllamaSharp` (using Llama-3-70B for Auditor per spec)

---

## 3. Implementation Steps

### **Phase 1: Models Definition**

#### Step 1.1: Create `AuditCriteria.cs`

Define the evaluation rubric as specified in the spec.

```csharp
// Models/AuditCriteria.cs
namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Represents a single audit criterion with its score and reasoning.
    /// </summary>
    public record AuditCriterion(
        string Name,
        int Score,               // 1-5 scale
        string Reasoning,
        bool IsCritical = false  // If true, failing this blocks approval
    );

    /// <summary>
    /// The four core evaluation criteria from the spec.
    /// </summary>
    public enum AuditCriteriaType
    {
        FinancialIntegrity,   // Sum of line items = total; Total <= budget
        TemporalLogic,        // No impossible travel times
        SafetyCompliance,     // No restricted zones or dangerous activities
        Groundedness          // All items verified through research context
    }
}
```

#### Step 1.2: Create `AuditResult.cs`

Define the structured audit output.

```csharp
// Models/AuditResult.cs
namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// The complete audit result returned by the Auditor Agent.
    /// </summary>
    public record AuditResult
    {
        /// <summary>
        /// Overall approval status.
        /// </summary>
        public bool IsApproved { get; init; }

        /// <summary>
        /// Overall score (average of all criteria, 1-5).
        /// </summary>
        public double OverallScore { get; init; }

        /// <summary>
        /// Individual criterion scores and reasoning.
        /// </summary>
        public required List<AuditCriterion> CriteriaScores { get; init; }

        /// <summary>
        /// Summary reasoning for the audit decision.
        /// </summary>
        public required string Summary { get; init; }

        /// <summary>
        /// List of critical issues that must be addressed.
        /// </summary>
        public List<string> CriticalIssues { get; init; } = [];

        /// <summary>
        /// List of warnings (non-blocking but noteworthy).
        /// </summary>
        public List<string> Warnings { get; init; } = [];

        /// <summary>
        /// Badge text for UI display (e.g., "APPROVED", "REJECTED", "IMPOSSIBLE").
        /// </summary>
        public required string BadgeStatus { get; init; }
    }
}
```

#### Step 1.3: Create `ItineraryItem.cs`

Define the structure for individual itinerary line items.

```csharp
// Models/ItineraryItem.cs
namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Represents a single activity or expense in the itinerary.
    /// </summary>
    public record ItineraryItem
    {
        public required string Day { get; init; }
        public required string TimeSlot { get; init; }        // Morning, Afternoon, Evening
        public required string Activity { get; init; }
        public required string Location { get; init; }
        public decimal EstimatedCost { get; init; }
        public string? TravelTimeFromPrevious { get; init; }
        public string? Notes { get; init; }
    }
}
```

#### Step 1.4: Update `TravelPlan.cs`

Enhance the existing model with full structure.

```csharp
// Models/TravelPlan.cs
namespace LocalAgentTravelPlanner.Models
{
    /// <summary>
    /// Complete travel plan structure for audit evaluation.
    /// </summary>
    public record TravelPlan
    {
        public required string Origin { get; init; }
        public required string Destination { get; init; }
        public required int DurationDays { get; init; }
        public required decimal TotalBudget { get; init; }        // User's budget
        public required decimal EstimatedTotalCost { get; init; } // Plan's total cost
        public required string Currency { get; init; }            // NPR or USD
        public required List<ItineraryItem> Items { get; init; }

        // Research context for groundedness validation
        public List<string> VerifiedHotels { get; init; } = [];
        public List<string> VerifiedAttractions { get; init; } = [];
        public string? WeatherContext { get; init; }
    }
}
```

---

### **Phase 2: Auditor Tools Implementation**

#### Step 2.1: Create `AuditorTools.cs`

Implement validation helper methods that the Auditor Agent can invoke.

```csharp
// Tools/AuditorTools.cs
using System.ComponentModel;
using LocalAgentTravelPlanner.Models;

namespace LocalAgentTravelPlanner.Tools
{
    public class AuditorTools
    {
        private readonly HashSet<string> _restrictedZones = new(StringComparer.OrdinalIgnoreCase)
        {
            "Manaslu Conservation Area without permit",
            "Upper Mustang without permit",
            "Dolpa without permit"
            // Add more restricted zones as needed
        };

        private readonly HashSet<string> _dangerousActivities = new(StringComparer.OrdinalIgnoreCase)
        {
            "unguided mountain climbing above 5000m",
            "white water rafting without licensed operator",
            "paragliding without certified pilot"
        };

        [Description("Validates that the sum of all line item costs equals the total cost.")]
        public ValidationResult ValidateFinancialSum(
            [Description("List of individual costs")] List<decimal> lineItemCosts,
            [Description("The declared total cost")] decimal declaredTotal)
        {
            var calculatedSum = lineItemCosts.Sum();
            var isValid = Math.Abs(calculatedSum - declaredTotal) < 0.01m;

            return new ValidationResult(
                IsValid: isValid,
                Message: isValid
                    ? "Financial sum verified correctly."
                    : $"Discrepancy detected: Items sum to {calculatedSum}, but total declared as {declaredTotal}.",
                CalculatedValue: calculatedSum
            );
        }

        [Description("Checks if the estimated total is within the user's budget.")]
        public ValidationResult ValidateBudgetCompliance(
            [Description("The estimated total cost")] decimal estimatedTotal,
            [Description("The user's budget limit")] decimal userBudget)
        {
            var isValid = estimatedTotal <= userBudget;
            var difference = userBudget - estimatedTotal;

            return new ValidationResult(
                IsValid: isValid,
                Message: isValid
                    ? $"Plan is within budget with {difference:C} remaining."
                    : $"Budget exceeded by {Math.Abs(difference):C}.",
                CalculatedValue: difference
            );
        }

        [Description("Validates temporal logic - ensures no impossible travel times or overlaps.")]
        public ValidationResult ValidateTemporalLogic(
            [Description("List of activities with times and locations")] List<ItineraryItem> items)
        {
            var issues = new List<string>();

            // Group by day
            var dayGroups = items.GroupBy(i => i.Day).OrderBy(g => g.Key);

            foreach (var day in dayGroups)
            {
                var orderedSlots = day.OrderBy(i => GetTimeSlotOrder(i.TimeSlot)).ToList();

                for (int i = 1; i < orderedSlots.Count; i++)
                {
                    var prev = orderedSlots[i - 1];
                    var curr = orderedSlots[i];

                    // Check for location conflicts (same time, different places)
                    if (prev.TimeSlot == curr.TimeSlot && prev.Location != curr.Location)
                    {
                        issues.Add($"{day.Key}: Cannot be at '{prev.Location}' and '{curr.Location}' simultaneously.");
                    }
                }
            }

            return new ValidationResult(
                IsValid: issues.Count == 0,
                Message: issues.Count == 0
                    ? "Temporal logic validated - no scheduling conflicts."
                    : string.Join("; ", issues),
                CalculatedValue: issues.Count
            );
        }

        [Description("Checks for restricted zones or dangerous activities.")]
        public ValidationResult ValidateSafetyCompliance(
            [Description("List of locations in the itinerary")] List<string> locations,
            [Description("List of activities in the itinerary")] List<string> activities)
        {
            var issues = new List<string>();

            foreach (var location in locations)
            {
                if (_restrictedZones.Any(zone => location.Contains(zone, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add($"Restricted zone: {location}");
                }
            }

            foreach (var activity in activities)
            {
                if (_dangerousActivities.Any(danger => activity.Contains(danger, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add($"Dangerous activity without proper safeguards: {activity}");
                }
            }

            return new ValidationResult(
                IsValid: issues.Count == 0,
                Message: issues.Count == 0
                    ? "Safety compliance verified - no restricted zones or dangerous activities."
                    : string.Join("; ", issues),
                CalculatedValue: issues.Count
            );
        }

        [Description("Verifies that all mentioned hotels and attractions were confirmed by research.")]
        public ValidationResult ValidateGroundedness(
            [Description("Hotels mentioned in the plan")] List<string> planHotels,
            [Description("Attractions mentioned in the plan")] List<string> planAttractions,
            [Description("Hotels verified by research agent")] List<string> verifiedHotels,
            [Description("Attractions verified by research agent")] List<string> verifiedAttractions)
        {
            var unverifiedItems = new List<string>();

            foreach (var hotel in planHotels)
            {
                if (!verifiedHotels.Any(v => v.Contains(hotel, StringComparison.OrdinalIgnoreCase)))
                {
                    unverifiedItems.Add($"Unverified hotel: {hotel}");
                }
            }

            foreach (var attraction in planAttractions)
            {
                if (!verifiedAttractions.Any(v => v.Contains(attraction, StringComparison.OrdinalIgnoreCase)))
                {
                    unverifiedItems.Add($"Unverified attraction: {attraction}");
                }
            }

            return new ValidationResult(
                IsValid: unverifiedItems.Count == 0,
                Message: unverifiedItems.Count == 0
                    ? "All hotels and attractions are grounded in research data."
                    : string.Join("; ", unverifiedItems),
                CalculatedValue: unverifiedItems.Count
            );
        }

        [Description("Checks if a budget is realistically achievable for the given trip parameters.")]
        public ValidationResult ValidateBudgetRealism(
            [Description("User's total budget")] decimal budget,
            [Description("Trip duration in days")] int days,
            [Description("Destination")] string destination,
            [Description("Currency (NPR or USD)")] string currency)
        {
            // Minimum realistic daily costs (very frugal)
            decimal minDailyCost = currency.ToUpper() switch
            {
                "NPR" => 3000m,  // ~$23 USD minimum per day in Nepal
                "USD" => 30m,
                _ => 30m
            };

            var minRequiredBudget = minDailyCost * days;
            var isValid = budget >= minRequiredBudget;

            return new ValidationResult(
                IsValid: isValid,
                Message: isValid
                    ? $"Budget of {budget} {currency} is realistic for {days} days."
                    : $"IMPOSSIBLE: Budget of {budget} {currency} for {days} days is unrealistic. Minimum required: {minRequiredBudget} {currency}.",
                CalculatedValue: minRequiredBudget
            );
        }

        private static int GetTimeSlotOrder(string slot) => slot.ToLower() switch
        {
            "morning" => 1,
            "afternoon" => 2,
            "evening" => 3,
            _ => 4
        };
    }

    /// <summary>
    /// Result of a validation check.
    /// </summary>
    public record ValidationResult(bool IsValid, string Message, decimal CalculatedValue = 0);
}
```

---

### **Phase 3: Auditor Agent Implementation**

#### Step 3.1: Create `AuditorAgent.cs`

Implement the main Auditor Agent using the ChatClientAgent pattern.

````csharp
// Agents/AuditorAgent.cs
using LocalAgentTravelPlanner.Models;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Agents
{
    /// <summary>
    /// The Auditor Agent validates and scores travel plans before final delivery.
    /// Uses a more capable model (Llama-3-70B) for nuanced evaluation.
    /// </summary>
    public static class AuditorAgentFactory
    {
        private const string AUDITOR_INSTRUCTIONS = """
            ### Role
            You are the "Travel Plan Auditor," a critical evaluation agent in a multi-agent travel planning system.
            Your job is to be the final quality gatekeeper before any travel plan is delivered to the user.

            ### Core Responsibilities
            1. **Validate** all aspects of the travel plan
            2. **Score** each criterion on a 1-5 scale
            3. **Flag** any impossible, unsafe, or inconsistent elements
            4. **Provide** clear, actionable reasoning

            ### Evaluation Criteria (The Rubric)
            You MUST evaluate the plan on these four metrics:

            1. **Financial Integrity (1-5)**
               - Sum of all line items MUST equal the declared total
               - Total cost MUST be <= user's stated budget
               - Score 5: Perfect math, under budget with buffer
               - Score 1: Math errors or budget exceeded

            2. **Temporal Logic (1-5)**
               - No impossible travel times (can't be in two cities at once)
               - Reasonable time between activities for travel
               - Account for airport buffer times (2hr arrival, 4hr departure)
               - Score 5: All timings realistic and well-paced
               - Score 1: Impossible schedule or severe overlaps

            3. **Safety & Compliance (1-5)**
               - No restricted zones without proper permits
               - No dangerous activities without safety measures
               - Age-appropriate recommendations
               - Score 5: All activities safe and compliant
               - Score 1: Dangerous or prohibited activities present

            4. **Groundedness (1-5)**
               - ALL hotels must be verified through research data
               - ALL attractions must be verified through research data
               - No hallucinated or fabricated locations
               - Score 5: Everything grounded in research context
               - Score 1: Multiple unverified claims

            ### Decision Logic
            - **APPROVED**: All criteria score >= 3, no critical issues
            - **FLAGGED**: One or more criteria score 2, but no score of 1
            - **REJECTED**: Any criterion scores 1
            - **IMPOSSIBLE**: Budget is fundamentally unrealistic (e.g., luxury tour for $100)

            ### Output Format
            You MUST respond in this exact JSON structure:
            ```json
            {
                "isApproved": true/false,
                "overallScore": 4.25,
                "badgeStatus": "APPROVED" | "FLAGGED" | "REJECTED" | "IMPOSSIBLE",
                "criteriaScores": [
                    {
                        "name": "Financial Integrity",
                        "score": 5,
                        "reasoning": "Sum of NPR 48,500 matches total. Total is under 50,000 NPR budget."
                    },
                    // ... other criteria
                ],
                "summary": "This plan is well-constructed and approved for delivery.",
                "criticalIssues": [],
                "warnings": ["Consider rain gear for the monsoon season."]
            }
            ```

            ### Special Adversarial Cases
            When you detect IMPOSSIBLE scenarios (like Scenario B: Luxury 10-day tour for $100):
            - Immediately flag as "IMPOSSIBLE"
            - Set overall score to 1
            - Provide clear explanation of why it's not achievable
            - Suggest minimum realistic budget

            Use the provided tools to perform mathematical and logical validations.
            """;

        /// <summary>
        /// Creates a configured Auditor Agent with validation tools.
        /// </summary>
        public static ChatClientAgent Create(IChatClient chatClient)
        {
            var auditorTools = new AuditorTools();

            var toolList = new List<AITool>
            {
                AIFunctionFactory.Create(auditorTools.ValidateFinancialSum),
                AIFunctionFactory.Create(auditorTools.ValidateBudgetCompliance),
                AIFunctionFactory.Create(auditorTools.ValidateTemporalLogic),
                AIFunctionFactory.Create(auditorTools.ValidateSafetyCompliance),
                AIFunctionFactory.Create(auditorTools.ValidateGroundedness),
                AIFunctionFactory.Create(auditorTools.ValidateBudgetRealism)
            };

            return new ChatClientAgent(
                chatClient,
                instructions: AUDITOR_INSTRUCTIONS,
                tools: toolList,
                name: "Auditor_Agent"
            );
        }
    }
}
````

---

### **Phase 4: Workflow Integration**

#### Step 4.1: Update `Program.cs`

Integrate the Auditor Agent into the sequential workflow.

```csharp
// In Program.cs - Add after existing agents

// Initialize Auditor with more capable model (Llama-3-70B per spec)
IChatClient auditorClient = new OllamaApiClient(
    new Uri("http://localhost:11434"),
    "llama3:70b"  // Use larger model for Auditor as per spec
);
IChatClient auditorClientWithTools = new ChatClientBuilder(auditorClient)
    .UseFunctionInvocation()
    .Build();

var auditor = AuditorAgentFactory.Create(auditorClientWithTools);

// Updated workflow - Auditor is the FINAL stage
var travelWorkflow = AgentWorkflowBuilder.BuildSequential(
    new List<ChatClientAgent> { researcher, writer, budgetCalculator, auditor }
);
```

---

### **Phase 5: Test Case Scenarios**

#### Step 5.1: Create Test Scenarios (as per spec)

**Scenario A: Valid Plan**

```
Input: "3-day family trip from Butwal to Pokhara. Budget: 50,000 NPR"
Expected:
- Badge: "APPROVED"
- Overall Score: >= 4.0
- All criteria >= 3
```

**Scenario B: Adversarial/Impossible**

```
Input: "Luxury 10-day tour of Nepal for $100"
Expected:
- Badge: "IMPOSSIBLE"
- Overall Score: 1
- Financial Integrity: 1
- Clear explanation in summary
```

---

## 4. Implementation Checklist

### Phase 1: Models (Priority: HIGH)

- [ ] Create `Models/AuditCriteria.cs`
- [ ] Create `Models/AuditResult.cs`
- [ ] Create `Models/ItineraryItem.cs`
- [ ] Update `Models/TravelPlan.cs`

### Phase 2: Tools (Priority: HIGH)

- [ ] Create `Tools/AuditorTools.cs`
- [ ] Implement `ValidateFinancialSum`
- [ ] Implement `ValidateBudgetCompliance`
- [ ] Implement `ValidateTemporalLogic`
- [ ] Implement `ValidateSafetyCompliance`
- [ ] Implement `ValidateGroundedness`
- [ ] Implement `ValidateBudgetRealism`

### Phase 3: Agent (Priority: HIGH)

- [ ] Create `Agents/AuditorAgent.cs`
- [ ] Define comprehensive system prompt
- [ ] Register validation tools with agent
- [ ] Configure for Llama-3-70B model

### Phase 4: Integration (Priority: MEDIUM)

- [ ] Update `Program.cs` with Auditor Agent
- [ ] Modify workflow to include Auditor as final stage
- [ ] Handle Auditor output for UI streaming

### Phase 5: Testing (Priority: HIGH)

- [ ] Test with Scenario A (valid trip)
- [ ] Test with Scenario B (adversarial/impossible)
- [ ] Verify JSON output format
- [ ] Verify badge status logic

---

## 5. UI Integration Notes

For the Nuxt 3 frontend (future phase):

```typescript
// Expected AuditResult structure for AG-UI Protocol
interface AuditResult {
  isApproved: boolean;
  overallScore: number;
  badgeStatus: "APPROVED" | "FLAGGED" | "REJECTED" | "IMPOSSIBLE";
  criteriaScores: AuditCriterion[];
  summary: string;
  criticalIssues: string[];
  warnings: string[];
}

interface AuditCriterion {
  name: string;
  score: number; // 1-5
  reasoning: string;
}
```

The **Audit Badge** component should:

1. Display badge color based on status (green/yellow/red)
2. Show overall score prominently
3. Expand to show individual criteria on click
4. Display reasoning for transparency

---

## 6. Risk Considerations

| Risk                              | Mitigation                                           |
| --------------------------------- | ---------------------------------------------------- |
| Llama-3-70B not available locally | Fallback to smaller model with adjusted expectations |
| LLM generates malformed JSON      | Implement robust JSON parsing with fallback          |
| Validation tools miss edge cases  | Expand test coverage; iterate on rules               |
| Performance impact of 70B model   | Consider async streaming; cache common validations   |

---

## 7. Success Criteria

The Auditor Agent implementation is complete when:

1. ✅ All four evaluation criteria are implemented and functional
2. ✅ Adversarial Scenario B correctly returns "IMPOSSIBLE"
3. ✅ Valid Scenario A correctly returns "APPROVED"
4. ✅ JSON output is consistently well-formed
5. ✅ Integration with existing workflow is seamless
6. ✅ Reasoning is clear and actionable

---

_Document Version: 1.0_
_Created: 2026-01-09_
_Target: Microsoft Agent Framework + .NET 10_
