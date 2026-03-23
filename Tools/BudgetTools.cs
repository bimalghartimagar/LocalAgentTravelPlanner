using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// Budget calculation and financial analysis tools.
    /// All estimates are in USD by default. The LLM can convert to local currency
    /// using the approximate exchange rates provided.
    /// </summary>
    public class BudgetTools
    {
        [Description("Converts between common travel currencies using approximate rates.")]
        public string ConvertCurrency(
            [Description("Amount to convert")] decimal amount,
            [Description("Source currency code (e.g., USD, EUR, GBP, JPY, THB, INR)")] string fromCurrency,
            [Description("Target currency code (e.g., USD, EUR, GBP, JPY, THB, INR)")] string toCurrency)
        {
            var from = fromCurrency.ToUpper().Trim();
            var to = toCurrency.ToUpper().Trim();

            // Approximate rates to USD (updated periodically; the LLM should note these are estimates)
            var toUsd = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                { "USD", 1m },
                { "EUR", 1.08m },
                { "GBP", 1.27m },
                { "JPY", 0.0067m },
                { "CNY", 0.14m },
                { "INR", 0.012m },
                { "THB", 0.029m },
                { "VND", 0.000041m },
                { "KRW", 0.00075m },
                { "AUD", 0.65m },
                { "CAD", 0.74m },
                { "SGD", 0.75m },
                { "MXN", 0.058m },
                { "BRL", 0.20m },
                { "IDR", 0.000063m },
                { "NPR", 0.0075m },
                { "MYR", 0.22m },
                { "PHP", 0.018m },
                { "AED", 0.27m },
                { "TRY", 0.031m },
                { "ZAR", 0.055m },
                { "EGP", 0.020m },
                { "MAD", 0.10m },
                { "PEN", 0.27m },
                { "COP", 0.00024m },
                { "NZD", 0.60m },
                { "CHF", 1.13m },
            };

            if (!toUsd.ContainsKey(from) || !toUsd.ContainsKey(to))
            {
                return $"""
                    ⚠️ Currency not found: {(toUsd.ContainsKey(from) ? to : from)}
                    Supported: {string.Join(", ", toUsd.Keys.Order())}
                    Tip: Use xe.com or Google for live rates.
                    """;
            }

            // Convert: fromCurrency → USD → toCurrency
            decimal amountInUsd = amount * toUsd[from];
            decimal result = toUsd[to] != 0 ? amountInUsd / toUsd[to] : 0;

            decimal directRate = toUsd[to] != 0 ? toUsd[from] / toUsd[to] : 0;

            return $"""
                Currency Conversion:
                {amount:N2} {from} ≈ {result:N2} {to}
                Approximate Rate: 1 {from} ≈ {directRate:N4} {to}
                Note: Rates are approximate. Check xe.com for live rates before exchanging.
                """;
        }

        [Description("Calculates daily budget breakdown based on travel style. All amounts in USD.")]
        public string CalculateDailyBudget(
            [Description("Budget tier: Frugal, Medium, or HighEnd")] string tier,
            [Description("Number of days")] int days = 1,
            [Description("Currency code (default USD)")] string currency = "USD")
        {
            var tierLower = tier.ToLower().Trim();

            // USD-based daily estimates (global averages for mid-cost destinations)
            var (accommodation, food, transport, activities) = tierLower switch
            {
                "frugal" or "budget" => (15m, 10m, 5m, 8m),
                "medium" or "mid-range" or "midrange" or "comfort" => (60m, 25m, 15m, 30m),
                "highend" or "high-end" or "luxury" => (200m, 60m, 40m, 80m),
                _ => (15m, 10m, 5m, 8m) // Default to frugal
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

                Summary: {days} days at {tier} level ≈ {grandTotal:N0} {currency}
                Note: Actual costs vary significantly by destination. Southeast Asia/South Asia
                can be 50-70% cheaper; Western Europe/Japan can be 30-50% more expensive.
                """;
        }

        [Description("Validates if a budget is realistic for a trip. Amounts in USD.")]
        public string ValidateBudgetRealism(
            [Description("Total budget in USD")] decimal budget,
            [Description("Number of days")] int days,
            [Description("Currency code")] string currency = "USD")
        {
            // USD-based daily minimums (global baseline)
            decimal absoluteMinDaily = 15m;   // Survival-level in a cheap country
            decimal frugalDaily = 38m;        // Backpacker style
            decimal comfortDaily = 130m;      // Mid-range comfort
            decimal luxuryDaily = 380m;       // High-end travel

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
            [Description("Currency code")] string currency = "USD")
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
                - Book hostels or guesthouses instead of hotels (save 50-70%)
                - Stay slightly outside tourist areas (save 20-30%)
                - Use booking apps for last-minute deals
                - Consider homestays or Airbnb for longer stays

                🍽️ FOOD
                - Eat where locals eat — avoid tourist-trap restaurants
                - Try street food and market stalls for breakfast/lunch
                - Carry a reusable water bottle (save $3-5/day in some countries)
                - Self-cater breakfasts if accommodation has a kitchen

                🚗 TRANSPORT
                - Use public transit instead of taxis (save 60-80%)
                - Walk when distances are short
                - Book intercity buses/trains in advance for better fares
                - Share rides with other travelers

                🎯 ACTIVITIES
                - Prioritize free attractions (parks, markets, viewpoints, temples)
                - Look for city passes or combo tickets for discounts
                - Visit museums on free/discount days
                - Book activities directly, not through hotel concierge

                📊 Estimated Savings from {currentTier} tier:
                - Frugal tips could save: 30-40% of daily costs
                - Smart booking could save: 15-25% overall
                """;
        }
    }
}
