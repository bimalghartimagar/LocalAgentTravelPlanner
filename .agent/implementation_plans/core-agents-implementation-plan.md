# Core Agents Implementation Plan

> **Objective:** Refactor and complete the Researcher, Planner (Itinerary Writer), and Accountant (Budget Calculator) agents as proper, testable C# classes following the Microsoft Agent Framework patterns.

---

## 1. Current State Analysis

### Existing Code Issues

| File                        | Status             | Issues                                  |
| --------------------------- | ------------------ | --------------------------------------- |
| `Agents/ResearcherAgent.cs` | Empty shell        | No implementation                       |
| `Agents/ItineraryAgent.cs`  | Incomplete         | Compilation errors, undefined variables |
| `Program.cs`                | Inline definitions | Agents defined inline, not reusable     |
| `Tools/TravelTools.cs`      | Basic              | Only 2 tools, needs expansion           |
| `Models/TravelPlan.cs`      | Empty shell        | No properties defined                   |

### Target Architecture

```
LocalAgentTravelPlanner/
├── Agents/
│   ├── ResearcherAgentFactory.cs    # Research & data gathering
│   ├── PlannerAgentFactory.cs       # Itinerary writing
│   ├── AccountantAgentFactory.cs    # Budget calculations
│   └── AuditorAgentFactory.cs       # Validation (Phase 2)
├── Models/
│   ├── TravelRequest.cs             # User input model
│   ├── ResearchContext.cs           # Research output
│   ├── ItineraryDay.cs              # Single day plan
│   ├── BudgetBreakdown.cs           # Cost breakdown
│   └── TravelPlan.cs                # Complete plan model
├── Tools/
│   ├── ResearchTools.cs             # Research-specific tools
│   ├── BudgetTools.cs               # Budget calculation tools
│   └── TravelTools.cs               # General travel tools
└── Program.cs                       # Clean orchestration
```

---

## 2. Implementation Phases

### **Phase 1: Models Definition**

#### 1.1 Create `TravelRequest.cs`

```csharp
// Models/TravelRequest.cs
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
```

#### 1.2 Create `ResearchContext.cs`

```csharp
// Models/ResearchContext.cs
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

    public record VerifiedHotel(
        string Name,
        string Location,
        decimal PricePerNight,
        string Category  // Budget, Mid-range, Luxury
    );

    public record VerifiedAttraction(
        string Name,
        string Description,
        decimal? EntranceFee,
        string? RecommendedDuration
    );
}
```

#### 1.3 Create `ItineraryDay.cs`

```csharp
// Models/ItineraryDay.cs
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
```

#### 1.4 Create `BudgetBreakdown.cs`

```csharp
// Models/BudgetBreakdown.cs
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

    public record CategoryCost(
        string Category,
        decimal DailyAmount,
        decimal Total,
        string Notes
    );

    public record FullBudgetAnalysis
    {
        public required BudgetBreakdown Frugal { get; init; }
        public required BudgetBreakdown Medium { get; init; }
        public required BudgetBreakdown HighEnd { get; init; }
        public required string RecommendedTier { get; init; }
        public required string Reasoning { get; init; }
    }
}
```

#### 1.5 Update `TravelPlan.cs`

```csharp
// Models/TravelPlan.cs
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
```

---

### **Phase 2: Tools Expansion**

#### 2.1 Create `ResearchTools.cs`

```csharp
// Tools/ResearchTools.cs
using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    public class ResearchTools
    {
        [Description("Gets current weather and forecast for a destination.")]
        public string GetWeatherForecast(
            [Description("The city name")] string city,
            [Description("Number of days to forecast")] int days = 3)
        {
            // Simulated - in production, use OpenWeatherMap API
            return $"""
                Weather for {city} ({days}-day forecast):
                - Today: 24°C, Partly cloudy
                - Tomorrow: 22°C, Sunny
                - Day 3: 20°C, Light rain expected
                - Recommended: Light layers, bring rain jacket
                """;
        }

        [Description("Finds hotels in a destination within a budget range.")]
        public string SearchHotels(
            [Description("The destination city")] string city,
            [Description("Budget category: Budget, Mid-range, or Luxury")] string category)
        {
            // Simulated hotel data
            var hotels = category.ToLower() switch
            {
                "budget" => $"""
                    Hotels in {city} (Budget):
                    1. Lakeside Hostel - NPR 800/night - Dorms, breakfast included
                    2. Traveler's Inn - NPR 1,500/night - Private room, fan
                    3. Mountain View Guesthouse - NPR 1,200/night - Garden view
                    """,
                "mid-range" => $"""
                    Hotels in {city} (Mid-range):
                    1. Hotel Barahi - NPR 4,500/night - 3-star, pool
                    2. Temple Tree Resort - NPR 5,000/night - Spa, restaurant
                    3. Lakefront Resort - NPR 3,800/night - Lake view
                    """,
                "luxury" => $"""
                    Hotels in {city} (Luxury):
                    1. Fish Tail Lodge - NPR 15,000/night - 5-star, iconic
                    2. Tiger Mountain Pokhara Lodge - NPR 25,000/night - Boutique
                    3. Pavilions Himalayas - NPR 35,000/night - Eco-luxury
                    """,
                _ => $"No hotels found for category: {category}"
            };
            return hotels;
        }

        [Description("Finds top attractions and things to do at a destination.")]
        public string GetAttractions(
            [Description("The destination city")] string city)
        {
            // Simulated for Pokhara - extend for other cities
            if (city.ToLower().Contains("pokhara"))
            {
                return """
                    Top Attractions in Pokhara:
                    1. Phewa Lake - Free entry, boat ride NPR 500/hr
                    2. World Peace Pagoda - Free entry, 45min hike
                    3. Davis Falls - NPR 50 entry, unique waterfall
                    4. Sarangkot - NPR 100 entry, sunrise view point
                    5. Begnas Lake - Less crowded, NPR 300 boat ride
                    6. Gupteshwor Cave - NPR 100 entry, sacred cave
                    7. International Mountain Museum - NPR 400 entry
                    8. Paragliding - NPR 8,000-12,000, tandem flight
                    """;
            }
            return $"Attractions data for {city} not available in mock database.";
        }

        [Description("Gets transportation options between two cities in Nepal.")]
        public string GetTransportOptions(
            [Description("Origin city")] string fromCity,
            [Description("Destination city")] string toCity)
        {
            // Simulated for Butwal-Pokhara route
            if (fromCity.ToLower().Contains("butwal") && toCity.ToLower().Contains("pokhara"))
            {
                return """
                    Transport from Butwal to Pokhara:
                    1. Tourist Bus - NPR 600-800, 4 hours, comfortable
                    2. Local Bus - NPR 350, 4.5 hours, basic
                    3. Micro Bus - NPR 500, 3.5 hours, faster but cramped
                    4. Private Taxi - NPR 5,000-7,000, 3 hours, door-to-door
                    5. Shared Jeep - NPR 700, 3.5 hours
                    Note: Prithvi Highway route, scenic mountain views
                    """;
            }
            return $"Transport from {fromCity} to {toCity}: Estimated NPR 500-5000 depending on mode.";
        }

        [Description("Gets local food recommendations and average meal costs.")]
        public string GetFoodRecommendations(
            [Description("The destination city")] string city)
        {
            return $"""
                Food in {city}:
                - Street Food: NPR 50-150/meal (momos, chowmein)
                - Local Restaurant: NPR 200-400/meal (dal bhat, thali)
                - Mid-range Restaurant: NPR 500-1000/meal
                - Fine Dining: NPR 1500-3000/meal
                - Popular spots: Lakeside cafes, Busy Bee, Moondance
                """;
        }

        [Description("Gets safety information and travel advisories for a destination.")]
        public string GetSafetyInfo(
            [Description("The destination city or region")] string location)
        {
            return $"""
                Safety Info for {location}:
                - General: Safe for tourists, low crime rate
                - Health: Drink bottled water, altitude awareness above 2500m
                - Scams: Negotiate prices before services
                - Emergency: Tourist Police 1144, Nepal Police 100
                - Permits: None needed for general Pokhara area
                - Restricted: Upper Mustang requires special permit
                """;
        }
    }
}
```

#### 2.2 Create `BudgetTools.cs`

```csharp
// Tools/BudgetTools.cs
using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    public class BudgetTools
    {
        [Description("Converts between NPR and USD currencies.")]
        public string ConvertCurrency(
            [Description("Amount to convert")] decimal amount,
            [Description("Source currency (NPR or USD)")] string fromCurrency,
            [Description("Target currency (NPR or USD)")] string toCurrency)
        {
            // Approximate exchange rate
            const decimal NPR_TO_USD = 0.0075m;
            const decimal USD_TO_NPR = 133.5m;

            decimal result = (fromCurrency.ToUpper(), toCurrency.ToUpper()) switch
            {
                ("NPR", "USD") => amount * NPR_TO_USD,
                ("USD", "NPR") => amount * USD_TO_NPR,
                _ => amount
            };

            return $"{amount} {fromCurrency} = {result:F2} {toCurrency}";
        }

        [Description("Calculates daily budget breakdown based on travel style.")]
        public string CalculateDailyBudget(
            [Description("Budget tier: Frugal, Medium, or HighEnd")] string tier,
            [Description("Currency (NPR or USD)")] string currency = "NPR")
        {
            var breakdown = tier.ToLower() switch
            {
                "frugal" => new
                {
                    Accommodation = currency == "NPR" ? 1000m : 7.5m,
                    Food = currency == "NPR" ? 600m : 4.5m,
                    Transport = currency == "NPR" ? 300m : 2.25m,
                    Activities = currency == "NPR" ? 500m : 3.75m
                },
                "medium" => new
                {
                    Accommodation = currency == "NPR" ? 4000m : 30m,
                    Food = currency == "NPR" ? 1500m : 11.25m,
                    Transport = currency == "NPR" ? 800m : 6m,
                    Activities = currency == "NPR" ? 2000m : 15m
                },
                "highend" => new
                {
                    Accommodation = currency == "NPR" ? 20000m : 150m,
                    Food = currency == "NPR" ? 5000m : 37.5m,
                    Transport = currency == "NPR" ? 3000m : 22.5m,
                    Activities = currency == "NPR" ? 10000m : 75m
                },
                _ => throw new ArgumentException($"Unknown tier: {tier}")
            };

            var total = breakdown.Accommodation + breakdown.Food +
                       breakdown.Transport + breakdown.Activities;

            return $"""
                Daily Budget ({tier}) in {currency}:
                - Accommodation: {breakdown.Accommodation}
                - Food: {breakdown.Food}
                - Transport: {breakdown.Transport}
                - Activities: {breakdown.Activities}
                - TOTAL: {total} {currency}/day
                """;
        }

        [Description("Validates if a budget is realistic for a trip.")]
        public string ValidateBudgetRealism(
            [Description("Total budget")] decimal budget,
            [Description("Number of days")] int days,
            [Description("Currency")] string currency = "NPR")
        {
            decimal minDaily = currency.ToUpper() == "NPR" ? 2400m : 18m;
            decimal minRequired = minDaily * days;

            if (budget < minRequired)
            {
                return $"""
                    ⚠️ Budget Warning:
                    Your budget of {budget} {currency} for {days} days ({budget/days:F0}/day)
                    is below the minimum recommended ({minDaily}/day).
                    Minimum required: {minRequired} {currency}
                    Status: POTENTIALLY UNREALISTIC
                    """;
            }

            decimal dailyBudget = budget / days;
            string tier = currency.ToUpper() == "NPR"
                ? dailyBudget switch
                {
                    < 3000 => "Ultra-Frugal",
                    < 8000 => "Frugal",
                    < 15000 => "Medium",
                    _ => "High-End"
                }
                : dailyBudget switch
                {
                    < 25 => "Ultra-Frugal",
                    < 60 => "Frugal",
                    < 110 => "Medium",
                    _ => "High-End"
                };

            return $"""
                ✅ Budget Analysis:
                Total: {budget} {currency} for {days} days
                Daily: {dailyBudget:F0} {currency}/day
                Suggested Tier: {tier}
                Status: REALISTIC
                """;
        }

        [Description("Calculates total trip cost from line items.")]
        public string CalculateTripTotal(
            [Description("Comma-separated list of costs")] string costs)
        {
            var items = costs.Split(',')
                .Select(c => decimal.TryParse(c.Trim(), out var v) ? v : 0)
                .ToList();

            decimal total = items.Sum();

            return $"""
                Trip Cost Calculation:
                Items: {string.Join(" + ", items)}
                Total: {total}
                Number of line items: {items.Count}
                """;
        }
    }
}
```

#### 2.3 Update `TravelTools.cs`

```csharp
// Tools/TravelTools.cs - Enhanced version
using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    public class TravelTools
    {
        [Description("Gets the current weather for a specific city.")]
        public string GetWeather([Description("The city name, e.g., Pokhara")] string city)
        {
            // Simulated weather
            var weathers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pokhara"] = "24°C, Partly cloudy with clear views of Machapuchare",
                ["Kathmandu"] = "18°C, Hazy with light smog",
                ["Butwal"] = "28°C, Humid and sunny",
                ["Chitwan"] = "30°C, Hot and humid, good for jungle safari"
            };

            return weathers.TryGetValue(city, out var weather)
                ? $"Weather in {city}: {weather}"
                : $"Weather in {city}: 22°C, conditions unknown - check local forecast.";
        }

        [Description("Gets flight or transport cost estimates between two cities.")]
        public string GetFlightEstimate(
            [Description("Origin city")] string fromCity,
            [Description("Destination city")] string toCity)
        {
            // For Nepal domestic routes
            if (fromCity.ToLower().Contains("kathmandu") && toCity.ToLower().Contains("pokhara"))
            {
                return """
                    Kathmandu to Pokhara:
                    - Flight: $80-120 USD (25 min), scenic mountain views
                    - Tourist Bus: NPR 800-1200 (6-7 hours)
                    - Private Car: NPR 8000-12000 (5-6 hours)
                    """;
            }

            return $"Transport from {fromCity} to {toCity}: Estimated $50-200 depending on mode.";
        }

        [Description("Gets the travel time between two locations.")]
        public string GetTravelTime(
            [Description("Start location")] string from,
            [Description("End location")] string to,
            [Description("Mode: walk, drive, bus")] string mode = "drive")
        {
            // Simplified estimation
            return $"Travel time from {from} to {to} by {mode}: approximately 30-45 minutes.";
        }

        [Description("Gets local emergency contacts for a destination.")]
        public string GetEmergencyContacts([Description("The country or city")] string location)
        {
            if (location.ToLower().Contains("nepal") ||
                location.ToLower().Contains("pokhara") ||
                location.ToLower().Contains("kathmandu"))
            {
                return """
                    Nepal Emergency Contacts:
                    - Police: 100
                    - Tourist Police: 1144 (English speaking)
                    - Ambulance: 102
                    - Fire: 101
                    - Embassy assistance: Contact your embassy in Kathmandu
                    """;
            }
            return $"Emergency contacts for {location}: Research local emergency numbers.";
        }
    }
}
```

---

### **Phase 3: Agent Factories**

#### 3.1 Create `ResearcherAgentFactory.cs`

```csharp
// Agents/ResearcherAgentFactory.cs
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Agents
{
    public static class ResearcherAgentFactory
    {
        private const string RESEARCHER_INSTRUCTIONS = """
            ### Role
            You are the "Travel Researcher," a specialized AI agent in a multi-agent travel planning system.
            Your job is to gather comprehensive, accurate data about the destination.

            ### Core Responsibilities
            1. **Weather Research**: Get current conditions and forecasts
            2. **Transportation**: Find all options between origin and destination
            3. **Accommodations**: Research hotels across budget tiers
            4. **Attractions**: Identify must-visit places and hidden gems
            5. **Local Intel**: Food, safety, local tips

            ### Process
            1. Identify the origin and destination cities from the user's request
            2. Use your tools to gather data systematically
            3. Verify information is current and accurate
            4. Compile findings in a structured format

            ### Output Format
            Provide your research in this structure:

            ## Weather
            [Current conditions and forecast]

            ## Transportation Options
            [All ways to get from origin to destination with costs]

            ## Recommended Hotels
            ### Budget
            [List 2-3 options with prices]
            ### Mid-Range
            [List 2-3 options with prices]
            ### Luxury
            [List 2-3 options with prices]

            ## Must-Visit Attractions
            [Numbered list with entry fees and time needed]

            ## Food & Dining
            [Local recommendations and price ranges]

            ## Safety & Local Tips
            [Important information for travelers]

            ### Important
            - Use ONLY information from your tools - do not hallucinate
            - Include prices in LOCAL CURRENCY (NPR) and USD
            - Flag any safety concerns or restricted areas
            - DO NOT write the itinerary yourself - that's the Planner's job
            """;

        public static ChatClientAgent Create(IChatClient chatClient)
        {
            var researchTools = new ResearchTools();
            var generalTools = new TravelTools();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(researchTools.GetWeatherForecast),
                AIFunctionFactory.Create(researchTools.SearchHotels),
                AIFunctionFactory.Create(researchTools.GetAttractions),
                AIFunctionFactory.Create(researchTools.GetTransportOptions),
                AIFunctionFactory.Create(researchTools.GetFoodRecommendations),
                AIFunctionFactory.Create(researchTools.GetSafetyInfo),
                AIFunctionFactory.Create(generalTools.GetWeather),
                AIFunctionFactory.Create(generalTools.GetFlightEstimate),
                AIFunctionFactory.Create(generalTools.GetEmergencyContacts)
            };

            return new ChatClientAgent(
                chatClient,
                instructions: RESEARCHER_INSTRUCTIONS,
                tools: tools,
                name: "Researcher_Agent"
            );
        }
    }
}
```

#### 3.2 Create `PlannerAgentFactory.cs`

```csharp
// Agents/PlannerAgentFactory.cs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Agents
{
    public static class PlannerAgentFactory
    {
        private const string PLANNER_INSTRUCTIONS = """
            ### Role
            You are the "Itinerary Architect," a specialized AI agent responsible for crafting
            logical, well-paced travel itineraries from research data.

            ### Input
            You receive comprehensive research from the Research Agent including:
            - Weather conditions and forecasts
            - Transportation options and times
            - Verified hotels and their prices
            - Attractions with entry fees and durations
            - Food recommendations
            - Safety information

            ### Core Responsibilities
            1. **Synthesize** research into a day-by-day plan
            2. **Sequence** activities logically based on location and time
            3. **Adapt** to weather conditions
            4. **Pace** appropriately for the traveler type

            ### Processing Rules
            1. **Weather Adaptation**
               - Temp > 30°C: Indoor/shaded activities 12 PM - 4 PM
               - Temp < 15°C: Include warming breaks, suggest layers
               - Rain expected: Prioritize indoor attractions

            2. **Flight/Bus Buffer Logic**
               - Day 1: Nothing within 2 hours of arrival (allow for transport/check-in)
               - Last Day: Final activity ends 4 hours before departure

            3. **Pacing Guidelines**
               - Family: Moderate pace, include rest breaks
               - Solo/Couple: Can be more intensive
               - Default: 3-4 activities per day maximum

            4. **Geographic Logic**
               - Group nearby attractions on same day
               - Account for realistic travel times between sites
               - Don't schedule opposite ends of city on same morning

            ### Output Format
            For each day, use this exact structure:

            ## Day [X]: [Theme/Focus]
            **Date:** [If known]
            **Weather:** [Expected conditions]

            ### Morning (8:00 AM - 12:00 PM)
            - **Activity:** [What to do]
            - **Location:** [Where]
            - **Duration:** [How long]
            - **Cost:** [Amount in NPR]
            - **Why now:** [Weather/timing justification]

            ### Afternoon (12:00 PM - 5:00 PM)
            - **Activity:** [What to do]
            - **Location:** [Where]
            - **Duration:** [How long]
            - **Cost:** [Amount in NPR]
            - **Travel tip:** [How to get there from morning location]

            ### Evening (5:00 PM - 9:00 PM)
            - **Activity:** [What to do]
            - **Location:** [Where]
            - **Duration:** [How long]
            - **Cost:** [Amount in NPR]

            **Daily Budget Estimate:** [Total for the day]
            **Packing Note:** [Weather-appropriate clothing]

            ---

            ### Important Rules
            - ONLY use hotels and attractions verified in the research
            - Include estimated costs for EVERY activity
            - Be specific about locations and timings
            - Consider the user's budget tier when selecting options
            - DO NOT calculate the full budget - that's the Accountant's job
            """;

        public static ChatClientAgent Create(IChatClient chatClient)
        {
            // Planner doesn't need tools - works from research context
            return new ChatClientAgent(
                chatClient,
                instructions: PLANNER_INSTRUCTIONS,
                name: "Planner_Agent"
            );
        }
    }
}
```

#### 3.3 Create `AccountantAgentFactory.cs`

```csharp
// Agents/AccountantAgentFactory.cs
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Agents
{
    public static class AccountantAgentFactory
    {
        private const string ACCOUNTANT_INSTRUCTIONS = """
            ### Role
            You are the "Travel Finance Strategist," a specialized AI agent responsible for
            accurate budget calculations and financial analysis.

            ### Core Responsibilities
            1. **Calculate** precise costs from the itinerary
            2. **Validate** math consistency (line items = total)
            3. **Compare** against user's budget
            4. **Provide** tiered budget breakdowns

            ### Input
            You receive:
            - User's budget and currency (NPR or USD)
            - Complete itinerary with estimated costs
            - Research data with hotel and activity prices

            ### Calculation Rules
            1. **Sum ALL costs** mentioned in the itinerary
            2. **Include hidden costs**: tips (10%), transport between sites, meals
            3. **Apply buffer** of 10% for unexpected expenses
            4. **Convert currencies** when needed for comparison

            ### Budget Tier Definitions

            #### Frugal (Backpacker)
            - Accommodation: Hostels, guesthouses (NPR 800-1500/night)
            - Food: Street food, local eateries (NPR 500-800/day)
            - Transport: Public buses, walking
            - Activities: Free attractions, low-cost entries

            #### Medium (Comfort)
            - Accommodation: 3-star hotels (NPR 3000-5000/night)
            - Food: Local restaurants, cafes (NPR 1200-2000/day)
            - Transport: Tourist buses, occasional taxi
            - Activities: Major attractions, 1-2 guided experiences

            #### High-End (Luxury)
            - Accommodation: 4-5 star hotels (NPR 15000+/night)
            - Food: Fine dining, hotel restaurants (NPR 4000+/day)
            - Transport: Private vehicle, domestic flights
            - Activities: Private tours, premium experiences

            ### Output Format

            ## Budget Analysis for [Destination] - [X] Days

            ### Your Budget
            - **Stated Budget:** [Amount] [Currency]
            - **Per Day:** [Amount/days] [Currency]

            ### Itinerary Cost Breakdown
            | Category | Daily | Total |
            |----------|-------|-------|
            | Accommodation | X | X |
            | Food & Dining | X | X |
            | Transportation | X | X |
            | Activities | X | X |
            | Miscellaneous (10%) | X | X |
            | **TOTAL** | **X** | **X** |

            ### Budget Comparison
            - Estimated Total: [Amount]
            - Your Budget: [Amount]
            - Difference: [+/- Amount] ([under/over] budget)
            - Status: ✅ WITHIN BUDGET / ⚠️ OVER BUDGET

            ### Alternative Tiers

            #### Frugal Option
            - Daily: [Amount]
            - Total: [Amount]
            - Key savings: [How to reduce costs]

            #### Medium Option
            - Daily: [Amount]
            - Total: [Amount]
            - Best value: [Recommendations]

            #### High-End Option
            - Daily: [Amount]
            - Total: [Amount]
            - Worth it for: [Premium experiences]

            ### Recommendation
            [Which tier fits the budget, specific suggestions]

            ### Important
            - ALL math must be verifiable
            - Show your calculations
            - Flag any impossibly low budgets
            - Use NPR as primary currency, show USD equivalent
            """;

        public static ChatClientAgent Create(IChatClient chatClient)
        {
            var budgetTools = new BudgetTools();

            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(budgetTools.ConvertCurrency),
                AIFunctionFactory.Create(budgetTools.CalculateDailyBudget),
                AIFunctionFactory.Create(budgetTools.ValidateBudgetRealism),
                AIFunctionFactory.Create(budgetTools.CalculateTripTotal)
            };

            return new ChatClientAgent(
                chatClient,
                instructions: ACCOUNTANT_INSTRUCTIONS,
                tools: tools,
                name: "Accountant_Agent"
            );
        }
    }
}
```

---

### **Phase 4: Update Program.cs**

```csharp
// Program.cs - Clean, refactored version
using LocalAgentTravelPlanner.Agents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OllamaSharp;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("🌍 Multi-Agent Travel Planner");
        Console.WriteLine("============================\n");

        // Initialize Ollama client (same model for worker agents)
        var ollamaUri = new Uri("http://localhost:11434");
        const string workerModel = "qwen2.5:7b";

        IChatClient baseClient = new OllamaApiClient(ollamaUri, workerModel);
        IChatClient chatClientWithTools = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();

        // Create specialized agents using factories
        var researcher = ResearcherAgentFactory.Create(chatClientWithTools);
        var planner = PlannerAgentFactory.Create(chatClientWithTools);
        var accountant = AccountantAgentFactory.Create(chatClientWithTools);
        // var auditor = AuditorAgentFactory.Create(auditorClient); // Phase 2

        Console.WriteLine("✅ Agents initialized:");
        Console.WriteLine("   - Researcher Agent (data gathering)");
        Console.WriteLine("   - Planner Agent (itinerary creation)");
        Console.WriteLine("   - Accountant Agent (budget analysis)");
        Console.WriteLine();

        // Build sequential workflow
        var workflow = AgentWorkflowBuilder.BuildSequential(
            new List<ChatClientAgent> { researcher, planner, accountant }
        );

        // Get user input
        Console.Write("📍 Where do you want to go? (e.g., '3-day family trip from Butwal to Pokhara, budget 50000 NPR')\n> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("No input provided. Exiting.");
            return;
        }

        Console.WriteLine("\n🔄 Processing your request...\n");
        Console.WriteLine("─".PadRight(60, '─'));

        // Execute workflow with streaming
        StreamingRun run = await InProcessExecution.StreamAsync(workflow, input);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        string currentAgent = "";
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentRunUpdateEvent e)
            {
                // Track which agent is active (for UI)
                if (e.Source != currentAgent && !string.IsNullOrEmpty(e.Source))
                {
                    currentAgent = e.Source;
                    Console.WriteLine($"\n\n🤖 [{currentAgent}]\n");
                }
                Console.Write(e.Data);
            }
            else if (evt is WorkflowOutputEvent)
            {
                break;
            }
        }

        Console.WriteLine("\n\n─".PadRight(60, '─'));
        Console.WriteLine("✅ Travel plan complete!");
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
```

---

## 3. Implementation Checklist

### Phase 1: Models ✅

- [ ] Create `Models/TravelRequest.cs`
- [ ] Create `Models/ResearchContext.cs`
- [ ] Create `Models/ItineraryDay.cs`
- [ ] Create `Models/BudgetBreakdown.cs`
- [ ] Update `Models/TravelPlan.cs`

### Phase 2: Tools ✅

- [ ] Create `Tools/ResearchTools.cs`
- [ ] Create `Tools/BudgetTools.cs`
- [ ] Update `Tools/TravelTools.cs`

### Phase 3: Agent Factories ✅

- [ ] Create `Agents/ResearcherAgentFactory.cs`
- [ ] Create `Agents/PlannerAgentFactory.cs`
- [ ] Create `Agents/AccountantAgentFactory.cs`
- [ ] Delete/archive old `ResearcherAgent.cs` and `ItineraryAgent.cs`

### Phase 4: Integration ✅

- [ ] Update `Program.cs`
- [ ] Test with sample input

---

## 4. Testing Scenarios

### Scenario 1: Standard Trip

```
Input: "3-day family trip from Butwal to Pokhara, budget 50000 NPR"
Expected: Complete research → logical itinerary → budget within limits
```

### Scenario 2: Budget Edge Case

```
Input: "5-day trip to Pokhara, budget 10000 NPR"
Expected: Accountant flags as "very tight budget", suggests ultra-frugal options
```

---

_Document Version: 1.0_
_Created: 2026-01-09_
