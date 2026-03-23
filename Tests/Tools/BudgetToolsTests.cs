using FluentAssertions;
using LocalAgentTravelPlanner.Tools;

namespace LocalAgentTravelPlanner.Tests.Tools
{
    /// <summary>
    /// Unit tests for BudgetTools.
    ///
    /// TESTING FINANCIAL CALCULATIONS:
    /// These tests verify the budget calculations are accurate.
    /// Financial errors in travel planning can ruin trips!
    /// </summary>
    public class BudgetToolsTests
    {
        private readonly BudgetTools _sut;

        public BudgetToolsTests()
        {
            _sut = new BudgetTools();
        }

        #region ConvertCurrency Tests

        [Theory]
        [InlineData(100, "USD", "EUR", "92")] // 100 * 1 / 1.08 ≈ 92.59
        [InlineData(100, "EUR", "USD", "108")] // 100 * 1.08 / 1 = 108
        [InlineData(100, "USD", "USD", "100")] // Same currency
        [InlineData(100, "GBP", "USD", "127")] // 100 * 1.27 = 127
        [InlineData(10000, "JPY", "USD", "67")] // 10000 * 0.0067 = 67
        public void ConvertCurrency_ShouldConvertCorrectly(
            decimal amount, string from, string to, string expectedContains)
        {
            var result = _sut.ConvertCurrency(amount, from, to);

            result.Should().Contain(expectedContains);
        }

        [Fact]
        public void ConvertCurrency_ShouldShowApproximateRate()
        {
            var result = _sut.ConvertCurrency(100, "USD", "EUR");

            result.Should().Contain("Approximate Rate");
        }

        [Fact]
        public void ConvertCurrency_UnsupportedCurrency_ShouldReturnError()
        {
            var result = _sut.ConvertCurrency(100, "USD", "XYZ");

            result.Should().Contain("Currency not found");
            result.Should().Contain("Supported");
        }

        #endregion

        #region CalculateDailyBudget Tests

        [Fact]
        public void CalculateDailyBudget_Frugal_ShouldReturnLowPrices()
        {
            var result = _sut.CalculateDailyBudget("frugal", 3, "USD");

            result.Should().Contain("Accommodation");
            result.Should().Contain("15"); // Frugal accommodation USD
            result.Should().Contain("3-Day Total");
        }

        [Fact]
        public void CalculateDailyBudget_HighEnd_ShouldReturnHighPrices()
        {
            var result = _sut.CalculateDailyBudget("luxury", 3, "USD");

            result.Should().Contain("200"); // Luxury accommodation USD
        }

        [Theory]
        [InlineData("frugal")]
        [InlineData("budget")]
        [InlineData("medium")]
        [InlineData("comfort")]
        [InlineData("highend")]
        [InlineData("luxury")]
        public void CalculateDailyBudget_AllTiers_ShouldWork(string tier)
        {
            var result = _sut.CalculateDailyBudget(tier, 1, "USD");

            result.Should().Contain("Daily Budget Breakdown");
            result.Should().Contain("GRAND TOTAL");
        }

        [Fact]
        public void CalculateDailyBudget_ShouldIncludeBuffer()
        {
            var result = _sut.CalculateDailyBudget("medium", 5, "USD");

            result.Should().Contain("Buffer (10%)");
        }

        #endregion

        #region ValidateBudgetRealism Tests

        [Fact]
        public void ValidateBudgetRealism_BelowMinimum_ShouldReturnImpossible()
        {
            // $10 total for 5 days = $2/day, well below $15 minimum
            var result = _sut.ValidateBudgetRealism(10, 5, "USD");

            result.Should().Contain("IMPOSSIBLE");
            result.Should().Contain("NOT feasible");
        }

        [Fact]
        public void ValidateBudgetRealism_ReasonableBudget_ShouldReturnRealistic()
        {
            // $500 for 5 days = $100/day
            var result = _sut.ValidateBudgetRealism(500, 5, "USD");

            result.Should().Contain("REALISTIC");
        }

        [Theory]
        [InlineData(100, 5, "Ultra-Frugal")]   // $20/day
        [InlineData(250, 5, "Frugal")]          // $50/day
        [InlineData(1000, 5, "Medium")]         // $200/day
        [InlineData(3000, 5, "High-End")]       // $600/day
        public void ValidateBudgetRealism_ShouldSuggestCorrectTier(
            decimal budget, int days, string expectedTier)
        {
            var result = _sut.ValidateBudgetRealism(budget, days, "USD");

            result.Should().Contain(expectedTier);
        }

        #endregion

        #region CalculateTripTotal Tests

        [Fact]
        public void CalculateTripTotal_ShouldSumCorrectly()
        {
            string costs = "100, 200, 300, 400";

            var result = _sut.CalculateTripTotal(costs, "USD");

            result.Should().Contain("Subtotal: 1,000 USD");
        }

        [Fact]
        public void CalculateTripTotal_ShouldIncludeBuffer()
        {
            string costs = "1000";

            var result = _sut.CalculateTripTotal(costs, "USD");

            result.Should().Contain("Recommended Buffer (10%): 100 USD");
            result.Should().Contain("Grand Total: 1,100 USD");
        }

        [Fact]
        public void CalculateTripTotal_WithInvalidInput_ShouldHandleGracefully()
        {
            string costs = "not, numbers, here";

            var result = _sut.CalculateTripTotal(costs, "USD");

            result.Should().Contain("No valid cost items found");
        }

        [Fact]
        public void CalculateTripTotal_ShouldShowStatistics()
        {
            string costs = "100, 500, 300";

            var result = _sut.CalculateTripTotal(costs, "USD");

            result.Should().Contain("Number of items: 3");
            result.Should().Contain("Highest item: 500");
            result.Should().Contain("Lowest item: 100");
        }

        #endregion

        #region GetSavingsTips Tests

        [Fact]
        public void GetSavingsTips_ShouldReturnAllCategories()
        {
            var result = _sut.GetSavingsTips("medium", 20);

            result.Should().Contain("ACCOMMODATION");
            result.Should().Contain("FOOD");
            result.Should().Contain("TRANSPORT");
            result.Should().Contain("ACTIVITIES");
        }

        [Fact]
        public void GetSavingsTips_ShouldMentionTargetPercent()
        {
            var result = _sut.GetSavingsTips("medium", 25);

            result.Should().Contain("Target: 25% savings");
        }

        #endregion
    }
}
