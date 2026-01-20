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
        [InlineData(1000, "NPR", "USD", "7.50")] // 1000 NPR * 0.0075 = 7.50 USD
        [InlineData(100, "USD", "NPR", "13,350")] // 100 USD * 133.5 = 13,350 NPR
        [InlineData(1000, "NPR", "NPR", "1,000")] // Same currency
        [InlineData(100, "USD", "USD", "100")] // Same currency
        public void ConvertCurrency_ShouldConvertCorrectly(
            decimal amount, string from, string to, string expectedContains)
        {
            // Act
            var result = _sut.ConvertCurrency(amount, from, to);

            // Assert
            result.Should().Contain(expectedContains);
        }

        [Fact]
        public void ConvertCurrency_ShouldShowExchangeRate()
        {
            // Act
            var result = _sut.ConvertCurrency(1000, "NPR", "USD");

            // Assert
            result.Should().Contain("Exchange Rate: 1 USD = 133.5 NPR");
        }

        #endregion

        #region CalculateDailyBudget Tests

        [Fact]
        public void CalculateDailyBudget_Frugal_ShouldReturnLowPrices()
        {
            // Act
            var result = _sut.CalculateDailyBudget("frugal", 3, "NPR");

            // Assert
            result.Should().Contain("Accommodation");
            result.Should().Contain("1,000"); // Frugal accommodation
            result.Should().Contain("3-Day Total");
        }

        [Fact]
        public void CalculateDailyBudget_HighEnd_ShouldReturnHighPrices()
        {
            // Act
            var result = _sut.CalculateDailyBudget("luxury", 3, "NPR");

            // Assert
            result.Should().Contain("20,000"); // Luxury accommodation
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
            // Act
            var result = _sut.CalculateDailyBudget(tier, 1, "NPR");

            // Assert
            result.Should().Contain("Daily Budget Breakdown");
            result.Should().Contain("GRAND TOTAL");
        }

        [Fact]
        public void CalculateDailyBudget_ShouldIncludeBuffer()
        {
            // Act
            var result = _sut.CalculateDailyBudget("medium", 5, "NPR");

            // Assert
            result.Should().Contain("Buffer (10%)");
        }

        #endregion

        #region ValidateBudgetRealism Tests

        [Fact]
        public void ValidateBudgetRealism_BelowMinimum_ShouldReturnImpossible()
        {
            // Arrange: Very low budget
            decimal budget = 1000; // NPR per day needed is ~1500
            int days = 5;

            // Act
            var result = _sut.ValidateBudgetRealism(budget, days, "NPR");

            // Assert
            result.Should().Contain("IMPOSSIBLE");
            result.Should().Contain("NOT feasible");
        }

        [Fact]
        public void ValidateBudgetRealism_ReasonableBudget_ShouldReturnRealistic()
        {
            // Arrange: Good budget
            decimal budget = 50000;
            int days = 5;

            // Act
            var result = _sut.ValidateBudgetRealism(budget, days, "NPR");

            // Assert
            result.Should().Contain("REALISTIC");
        }

        [Theory]
        [InlineData(10000, 5, "Ultra-Frugal")] // 2000/day
        [InlineData(20000, 5, "Frugal")] // 4000/day
        [InlineData(50000, 5, "Medium")] // 10000/day
        [InlineData(300000, 5, "High-End")] // 60000/day
        public void ValidateBudgetRealism_ShouldSuggestCorrectTier(
            decimal budget, int days, string expectedTier)
        {
            // Act
            var result = _sut.ValidateBudgetRealism(budget, days, "NPR");

            // Assert
            result.Should().Contain(expectedTier);
        }

        #endregion

        #region CalculateTripTotal Tests

        [Fact]
        public void CalculateTripTotal_ShouldSumCorrectly()
        {
            // Arrange
            string costs = "1000, 2000, 3000, 4000";

            // Act
            var result = _sut.CalculateTripTotal(costs, "NPR");

            // Assert
            result.Should().Contain("Subtotal: 10,000 NPR");
        }

        [Fact]
        public void CalculateTripTotal_ShouldIncludeBuffer()
        {
            // Arrange
            string costs = "10000";

            // Act
            var result = _sut.CalculateTripTotal(costs, "NPR");

            // Assert
            result.Should().Contain("Recommended Buffer (10%): 1,000 NPR");
            result.Should().Contain("Grand Total: 11,000 NPR");
        }

        [Fact]
        public void CalculateTripTotal_WithInvalidInput_ShouldHandleGracefully()
        {
            // Arrange
            string costs = "not, numbers, here";

            // Act
            var result = _sut.CalculateTripTotal(costs, "NPR");

            // Assert
            result.Should().Contain("No valid cost items found");
        }

        [Fact]
        public void CalculateTripTotal_ShouldShowStatistics()
        {
            // Arrange
            string costs = "1000, 5000, 3000";

            // Act
            var result = _sut.CalculateTripTotal(costs, "NPR");

            // Assert
            result.Should().Contain("Number of items: 3");
            result.Should().Contain("Highest item: 5,000");
            result.Should().Contain("Lowest item: 1,000");
        }

        #endregion

        #region GetSavingsTips Tests

        [Fact]
        public void GetSavingsTips_ShouldReturnAllCategories()
        {
            // Act
            var result = _sut.GetSavingsTips("medium", 20);

            // Assert
            result.Should().Contain("ACCOMMODATION");
            result.Should().Contain("FOOD");
            result.Should().Contain("TRANSPORT");
            result.Should().Contain("ACTIVITIES");
        }

        [Fact]
        public void GetSavingsTips_ShouldMentionTargetPercent()
        {
            // Act
            var result = _sut.GetSavingsTips("medium", 25);

            // Assert
            result.Should().Contain("Target: 25% savings");
        }

        #endregion
    }
}
