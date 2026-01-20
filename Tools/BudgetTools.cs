using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// Budget calculation and financial analysis tools.
    /// </summary>
    public class BudgetTools
    {
        [Description("Converts between NPR and USD currencies.")]
        public string ConvertCurrency(
            [Description("Amount to convert")] decimal amount,
            [Description("Source currency (NPR or USD)")] string fromCurrency,
            [Description("Target currency (NPR or USD)")] string toCurrency)
        {
            // Approximate exchange rate (as of 2024)
            const decimal NPR_TO_USD = 0.0075m;
            const decimal USD_TO_NPR = 133.5m;

            var from = fromCurrency.ToUpper().Trim();
            var to = toCurrency.ToUpper().Trim();

            decimal result = (from, to) switch
            {
                ("NPR", "USD") => amount * NPR_TO_USD,
                ("USD", "NPR") => amount * USD_TO_NPR,
                ("NPR", "NPR") => amount,
                ("USD", "USD") => amount,
                _ => amount
            };

            return $"""
                Currency Conversion:
                {amount:N2} {from} = {result:N2} {to}
                Exchange Rate: 1 USD = 133.5 NPR
                """;
        }

        [Description("Calculates daily budget breakdown based on travel style.")]
        public string CalculateDailyBudget(
            [Description("Budget tier: Frugal, Medium, or HighEnd")] string tier,
            [Description("Number of days")] int days = 1,
            [Description("Currency (NPR or USD)")] string currency = "NPR")
        {
            var tierLower = tier.ToLower().Trim();
            var isNPR = currency.ToUpper() == "NPR";

            var (accommodation, food, transport, activities) = tierLower switch
            {
                "frugal" or "budget" => (
                    isNPR ? 1000m : 7.5m,
                    isNPR ? 600m : 4.5m,
                    isNPR ? 300m : 2.25m,
                    isNPR ? 500m : 3.75m
                ),
                "medium" or "mid-range" or "midrange" or "comfort" => (
                    isNPR ? 4000m : 30m,
                    isNPR ? 1500m : 11.25m,
                    isNPR ? 800m : 6m,
                    isNPR ? 2000m : 15m
                ),
                "highend" or "high-end" or "luxury" => (
                    isNPR ? 20000m : 150m,
                    isNPR ? 5000m : 37.5m,
                    isNPR ? 3000m : 22.5m,
                    isNPR ? 10000m : 75m
                ),
                _ => (1000m, 600m, 300m, 500m) // Default to frugal
            };

            var dailyTotal = accommodation + food + transport + activities;
            var tripTotal = dailyTotal * days;
            var miscellaneous = tripTotal * 0.1m; // 10% buffer
            var grandTotal = tripTotal + miscellaneous;

            return $"""
                Daily Budget Breakdown ({tier}) in {currency}:
                
                | Category       | Daily      | {days}-Day Total |
                |----------------|------------|------------------|
                | Accommodation  | {accommodation,10:N0} | {accommodation * days,16:N0} |
                | Food & Dining  | {food,10:N0} | {food * days,16:N0} |
                | Transportation | {transport,10:N0} | {transport * days,16:N0} |
                | Activities     | {activities,10:N0} | {activities * days,16:N0} |
                | SUBTOTAL       | {dailyTotal,10:N0} | {tripTotal,16:N0} |
                | Buffer (10%)   |     —      | {miscellaneous,16:N0} |
                | GRAND TOTAL    |     —      | {grandTotal,16:N0} |

                Summary: {days} days at {tier} level = {grandTotal:N0} {currency}
                """;
        }

        [Description("Validates if a budget is realistic for a trip.")]
        public string ValidateBudgetRealism(
            [Description("Total budget")] decimal budget,
            [Description("Number of days")] int days,
            [Description("Currency")] string currency = "NPR")
        {
            var isNPR = currency.ToUpper() == "NPR";

            // Minimum daily costs for survival-level travel
            decimal absoluteMinDaily = isNPR ? 1500m : 12m;
            decimal frugalDaily = isNPR ? 2400m : 18m;
            decimal comfortDaily = isNPR ? 8300m : 62m;
            decimal luxuryDaily = isNPR ? 38000m : 285m;

            decimal minRequired = absoluteMinDaily * days;
            decimal dailyBudget = budget / days;

            if (budget < minRequired)
            {
                return $"""
                    ❌ BUDGET ASSESSMENT: IMPOSSIBLE
                    
                    Your Budget: {budget:N0} {currency} for {days} days
                    Daily Budget: {dailyBudget:N0} {currency}/day
                    
                    ⚠️ This budget is below the absolute minimum required.
                    - Minimum needed: {minRequired:N0} {currency} ({absoluteMinDaily:N0}/day)
                    - Shortfall: {minRequired - budget:N0} {currency}
                    
                    This trip is NOT feasible with the stated budget.
                    Consider: Reducing days, increasing budget, or choosing a cheaper destination.
                    """;
            }

            string tier;
            string assessment;
            
            if (dailyBudget < frugalDaily)
            {
                tier = "Ultra-Frugal (Challenging)";
                assessment = "Very tight budget. Must use hostels, eat street food only, skip paid attractions.";
            }
            else if (dailyBudget < comfortDaily)
            {
                tier = "Frugal (Backpacker)";
                assessment = "Achievable. Budget guesthouses, local food, selective paid activities.";
            }
            else if (dailyBudget < luxuryDaily)
            {
                tier = "Medium (Comfortable)";
                assessment = "Good budget. Nice hotels, varied dining, most attractions accessible.";
            }
            else
            {
                tier = "High-End (Luxury)";
                assessment = "Generous budget. Premium accommodations, fine dining, all experiences accessible.";
            }

            decimal remaining = budget - (frugalDaily * days);

            return $"""
                ✅ BUDGET ASSESSMENT: REALISTIC
                
                Your Budget: {budget:N0} {currency} for {days} days
                Daily Budget: {dailyBudget:N0} {currency}/day
                
                Suggested Tier: {tier}
                Assessment: {assessment}
                
                Comparison to Tiers:
                - Frugal minimum: {frugalDaily * days:N0} {currency} ({(budget >= frugalDaily * days ? "✓" : "✗")})
                - Comfort minimum: {comfortDaily * days:N0} {currency} ({(budget >= comfortDaily * days ? "✓" : "✗")})
                - Luxury minimum: {luxuryDaily * days:N0} {currency} ({(budget >= luxuryDaily * days ? "✓" : "✗")})
                
                Budget Headroom: {(remaining > 0 ? $"+{remaining:N0}" : remaining.ToString("N0"))} {currency} above frugal tier
                """;
        }

        [Description("Calculates total trip cost from individual line items.")]
        public string CalculateTripTotal(
            [Description("Comma-separated list of costs")] string costs,
            [Description("Currency")] string currency = "NPR")
        {
            var items = costs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => decimal.TryParse(c.Trim(), out var v) ? v : 0)
                .Where(v => v > 0)
                .ToList();

            if (items.Count == 0)
            {
                return "No valid cost items found. Please provide comma-separated numbers.";
            }

            decimal total = items.Sum();
            decimal average = items.Average();
            decimal buffer = total * 0.1m;

            return $"""
                Trip Cost Calculation:
                
                Line Items: {string.Join(" + ", items.Select(i => i.ToString("N0")))}
                
                Subtotal: {total:N0} {currency}
                Recommended Buffer (10%): {buffer:N0} {currency}
                Grand Total: {total + buffer:N0} {currency}
                
                Statistics:
                - Number of items: {items.Count}
                - Average per item: {average:N0} {currency}
                - Highest item: {items.Max():N0} {currency}
                - Lowest item: {items.Min():N0} {currency}
                """;
        }

        [Description("Suggests cost-saving tips for a given budget tier.")]
        public string GetSavingsTips(
            [Description("Current budget tier")] string currentTier,
            [Description("Target savings percentage")] int savingsPercent = 20)
        {
            return $"""
                💰 Cost-Saving Tips (Target: {savingsPercent}% savings):

                🏨 ACCOMMODATION
                - Book guesthouses instead of hotels (save 50-70%)
                - Stay slightly outside tourist areas (save 20-30%)
                - Use booking apps for last-minute deals
                - Consider homestays for authentic experience

                🍽️ FOOD
                - Eat Dal Bhat (unlimited refills, best value)
                - Buy street food for breakfast/snacks
                - Carry water bottle and purification tablets
                - Avoid tourist-trap restaurants on main street

                🚗 TRANSPORT
                - Use local buses instead of tourist buses (save 40%)
                - Walk when distances are short
                - Share taxis with other travelers
                - Negotiate all taxi fares before boarding

                🎯 ACTIVITIES
                - Choose free attractions (temples, lakes, viewpoints)
                - Hike to World Peace Pagoda instead of taking taxi
                - Visit museums on discount days
                - Book activities directly, not through hotels

                📊 Estimated Savings from {currentTier} tier:
                - Frugal tips could save: 30-40% of daily costs
                - Smart booking could save: 15-25% overall
                """;
        }
    }
}
