using FluentAssertions;
using LocalAgentTravelPlanner.Tools;

namespace LocalAgentTravelPlanner.Tests.Tools
{
    /// <summary>
    /// Unit tests for AuditorTools.
    ///
    /// TEST DESIGN PHILOSOPHY:
    /// Each test follows the AAA pattern: Arrange, Act, Assert
    /// Tests are named: MethodName_Scenario_ExpectedResult
    ///
    /// WHY TEST THESE TOOLS?
    /// 1. Tools are deterministic code - perfect for unit testing
    /// 2. The LLM depends on correct tool output for good decisions
    /// 3. Regression prevention - ensure math/logic doesn't break
    /// 4. Documentation - tests show how tools should behave
    /// </summary>
    public class AuditorToolsTests
    {
        private readonly AuditorTools _sut; // System Under Test

        public AuditorToolsTests()
        {
            _sut = new AuditorTools();
        }

        #region ValidateMathConsistency Tests

        [Fact]
        public void ValidateMathConsistency_WhenSumMatchesTotal_ShouldPass()
        {
            // Arrange
            string costs = "1000, 2000, 3000";
            decimal statedTotal = 6000;

            // Act
            var result = _sut.ValidateMathConsistency(costs, statedTotal);

            // Assert
            result.Should().Contain("PASS");
            result.Should().Contain("6,000");
        }

        [Fact]
        public void ValidateMathConsistency_WhenWithinTolerance_ShouldPass()
        {
            // Arrange: Sum is 6000, stated total is 6200 (within 5% tolerance)
            string costs = "1000, 2000, 3000";
            decimal statedTotal = 6200; // 3.3% difference, within 5%

            // Act
            var result = _sut.ValidateMathConsistency(costs, statedTotal);

            // Assert
            result.Should().Contain("PASS");
        }

        [Fact]
        public void ValidateMathConsistency_WhenSignificantDiscrepancy_ShouldFail()
        {
            // Arrange: Sum is 6000, stated total is 10000 (66% off)
            string costs = "1000, 2000, 3000";
            decimal statedTotal = 10000;

            // Act
            var result = _sut.ValidateMathConsistency(costs, statedTotal);

            // Assert
            result.Should().Contain("FAIL");
            result.Should().Contain("discrepancy");
        }

        [Fact]
        public void ValidateMathConsistency_WhenNoCosts_ShouldReturnError()
        {
            // Arrange
            string costs = "";
            decimal statedTotal = 5000;

            // Act
            var result = _sut.ValidateMathConsistency(costs, statedTotal);

            // Assert
            result.Should().Contain("ERROR");
            result.Should().Contain("No valid cost items");
        }

        [Fact]
        public void ValidateMathConsistency_WithCurrencySymbols_ShouldParsCorrectly()
        {
            // Arrange: Include NPR symbols
            // Note: The regex strips all non-digit/non-decimal characters
            // So "1,000" becomes "1000" correctly
            string costs = "NPR 1000, NPR 2000, 3000";
            decimal statedTotal = 6000;

            // Act
            var result = _sut.ValidateMathConsistency(costs, statedTotal);

            // Assert
            result.Should().Contain("PASS");
        }

        #endregion

        #region ValidateBudgetFit Tests

        [Fact]
        public void ValidateBudgetFit_WhenWellUnderBudget_ShouldShowGreenStatus()
        {
            // Arrange
            decimal estimatedCost = 40000;
            decimal userBudget = 60000;

            // Act
            var result = _sut.ValidateBudgetFit(estimatedCost, userBudget);

            // Assert
            result.Should().Contain("WELL WITHIN BUDGET");
            result.Should().Contain("Room for upgrades");
        }

        [Fact]
        public void ValidateBudgetFit_WhenSlightlyOver_ShouldShowWarning()
        {
            // Arrange: 108% of budget
            decimal estimatedCost = 54000;
            decimal userBudget = 50000;

            // Act
            var result = _sut.ValidateBudgetFit(estimatedCost, userBudget);

            // Assert
            result.Should().Contain("OVER BUDGET");
            result.Should().Contain("Cost reductions required");
        }

        [Fact]
        public void ValidateBudgetFit_WhenSignificantlyOver_ShouldShowError()
        {
            // Arrange: 150% of budget
            decimal estimatedCost = 75000;
            decimal userBudget = 50000;

            // Act
            var result = _sut.ValidateBudgetFit(estimatedCost, userBudget);

            // Assert
            result.Should().Contain("SIGNIFICANTLY OVER BUDGET");
            result.Should().Contain("Major budget revision");
        }

        [Theory]
        [InlineData(40000, 50000, "WELL WITHIN BUDGET")]
        [InlineData(47000, 50000, "WITHIN BUDGET")]
        [InlineData(51000, 50000, "TIGHT FIT")]
        [InlineData(55000, 50000, "OVER BUDGET")]
        [InlineData(70000, 50000, "SIGNIFICANTLY OVER BUDGET")]
        public void ValidateBudgetFit_VariousBudgetScenarios_ShouldReturnCorrectStatus(
            decimal estimatedCost, decimal userBudget, string expectedStatus)
        {
            // Act
            var result = _sut.ValidateBudgetFit(estimatedCost, userBudget);

            // Assert
            result.Should().Contain(expectedStatus);
        }

        #endregion

        #region ValidateTravelTime Tests

        [Fact]
        public void ValidateTravelTime_KathmanduToPokhara_WithEnoughTime_ShouldPass()
        {
            // Arrange: 8 hours available for a 6-hour taxi ride
            string from = "Kathmandu";
            string to = "Pokhara";
            int availableMinutes = 480; // 8 hours
            string mode = "taxi";

            // Act
            var result = _sut.ValidateTravelTime(from, to, availableMinutes, mode);

            // Assert
            result.Should().Contain("POSSIBLE");
            result.Should().Contain("360 minutes"); // Expected travel time
        }

        [Fact]
        public void ValidateTravelTime_KathmanduToPokhara_WithInsufficientTime_ShouldFail()
        {
            // Arrange: Only 2 hours for a 6-hour ride
            string from = "Kathmandu";
            string to = "Pokhara";
            int availableMinutes = 120;
            string mode = "taxi";

            // Act
            var result = _sut.ValidateTravelTime(from, to, availableMinutes, mode);

            // Assert
            result.Should().Contain("IMPOSSIBLE");
            result.Should().Contain("Insufficient time");
        }

        [Fact]
        public void ValidateTravelTime_WithFlight_ShouldBeQuick()
        {
            // Arrange: 1 hour for a 30-minute flight
            string from = "Kathmandu";
            string to = "Pokhara";
            int availableMinutes = 60;
            string mode = "flight";

            // Act
            var result = _sut.ValidateTravelTime(from, to, availableMinutes, mode);

            // Assert
            result.Should().Contain("POSSIBLE");
            result.Should().Contain("30 minutes");
        }

        [Fact]
        public void ValidateTravelTime_UnknownRoute_ShouldUseDefaultEstimate()
        {
            // Arrange: Unknown location
            string from = "SomePlace";
            string to = "AnotherPlace";
            int availableMinutes = 60;
            string mode = "taxi";

            // Act
            var result = _sut.ValidateTravelTime(from, to, availableMinutes, mode);

            // Assert
            result.Should().Contain("POSSIBLE"); // Default taxi time is 30 min
        }

        #endregion

        #region CheckGroundedness Tests

        [Fact]
        public void CheckGroundedness_WhenExactMatch_ShouldVerify()
        {
            // Arrange
            string itemName = "Hotel Pokhara Grande";
            string context = "Recommended hotels include Hotel Pokhara Grande and Lakeside Retreat.";

            // Act
            var result = _sut.CheckGroundedness(itemName, context);

            // Assert
            result.Should().Contain("VERIFIED");
            result.Should().Contain("HIGH");
        }

        [Fact]
        public void CheckGroundedness_WhenNotFound_ShouldFlagHallucination()
        {
            // Arrange
            string itemName = "Imaginary Paradise Resort";
            string context = "Hotels in the area include Hotel Pokhara Grande and Lakeside Retreat.";

            // Act
            var result = _sut.CheckGroundedness(itemName, context);

            // Assert
            result.Should().Contain("NOT FOUND");
            result.Should().Contain("hallucinated");
        }

        [Fact]
        public void CheckGroundedness_WithPartialMatch_ShouldShowWarning()
        {
            // Arrange: "Pokhara Resort" partially matches "Hotel Pokhara Grande"
            string itemName = "Pokhara Lake Resort";
            string context = "Hotels in Pokhara include Lake View Hotel and Mountain Resort.";

            // Act
            var result = _sut.CheckGroundedness(itemName, context);

            // Assert
            // Should have some word matches (Pokhara, Lake, Resort)
            result.Should().Contain("Word Matches:");
        }

        [Fact]
        public void CheckGroundedness_CaseInsensitive_ShouldWork()
        {
            // Arrange
            string itemName = "HOTEL POKHARA GRANDE";
            string context = "hotel pokhara grande is a nice place";

            // Act
            var result = _sut.CheckGroundedness(itemName, context);

            // Assert
            result.Should().Contain("VERIFIED");
        }

        #endregion

        #region CheckSafetyRequirements Tests

        [Fact]
        public void CheckSafetyRequirements_ForUpperMustang_ShouldRequirePermit()
        {
            // Arrange
            string location = "Upper Mustang trek";

            // Act
            var result = _sut.CheckSafetyRequirements(location);

            // Assert
            result.Should().Contain("Special Restricted Area Permit");
            result.Should().Contain("$500");
            result.Should().Contain("ATTENTION REQUIRED");
        }

        [Fact]
        public void CheckSafetyRequirements_ForPokhara_ShouldBeAllClear()
        {
            // Arrange
            string location = "Pokhara Lakeside";

            // Act
            var result = _sut.CheckSafetyRequirements(location);

            // Assert
            result.Should().Contain("CLEAR");
            result.Should().Contain("None required for standard tourism");
        }

        [Fact]
        public void CheckSafetyRequirements_ForEverest_ShouldWarnAboutAltitude()
        {
            // Arrange
            string location = "Everest Base Camp";

            // Act
            var result = _sut.CheckSafetyRequirements(location);

            // Assert
            result.Should().Contain("Altitude sickness");
            result.Should().Contain("ATTENTION REQUIRED");
        }

        #endregion

        #region DetermineAuditDecision Tests

        [Fact]
        public void DetermineAuditDecision_AllHighScores_ShouldApprove()
        {
            // Arrange
            int financial = 5, temporal = 4, safety = 5, grounded = 4;

            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            result.Should().Contain("APPROVED");
            result.Should().Contain("All criteria meet acceptable standards");
        }

        [Fact]
        public void DetermineAuditDecision_OneCriticalFailure_ShouldReject()
        {
            // Arrange: Financial score is 1 (critical)
            int financial = 1, temporal = 4, safety = 4, grounded = 4;

            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            result.Should().Contain("REJECTED");
            result.Should().Contain("Critical failure in: Financial Integrity");
        }

        [Fact]
        public void DetermineAuditDecision_OneLowScore_ShouldFlag()
        {
            // Arrange: Temporal score is 2 (low but not critical)
            int financial = 4, temporal = 2, safety = 4, grounded = 4;

            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            result.Should().Contain("FLAGGED");
            result.Should().Contain("Issues in: Temporal Logic");
        }

        [Fact]
        public void DetermineAuditDecision_VeryLowAverage_ShouldBeImpossible()
        {
            // Arrange: All scores are 1 except one
            int financial = 2, temporal = 1, safety = 1, grounded = 1;

            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            // Note: Because there are scores of 1, it will be REJECTED, not IMPOSSIBLE
            result.Should().Contain("REJECTED");
        }

        [Fact]
        public void DetermineAuditDecision_InvalidScores_ShouldReturnError()
        {
            // Arrange: Invalid score
            int financial = 6, temporal = 4, safety = 4, grounded = 4;

            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            result.Should().Contain("ERROR");
            result.Should().Contain("between 1 and 5");
        }

        [Theory]
        [InlineData(5, 5, 5, 5, "APPROVED")]
        [InlineData(3, 3, 3, 3, "APPROVED")]
        [InlineData(2, 3, 3, 3, "FLAGGED")]
        [InlineData(1, 5, 5, 5, "REJECTED")]
        [InlineData(1, 1, 5, 5, "REJECTED")]
        public void DetermineAuditDecision_VariousScenarios_ShouldReturnCorrectDecision(
            int financial, int temporal, int safety, int grounded, string expectedDecision)
        {
            // Act
            var result = _sut.DetermineAuditDecision(financial, temporal, safety, grounded);

            // Assert
            result.Should().Contain(expectedDecision);
        }

        #endregion

        #region CheckRelevance Tests

        [Fact]
        public void CheckRelevance_WhenAllRequirementsMatched_ShouldBeHighlyRelevant()
        {
            // Arrange
            var request = "3-day family trip from Kathmandu to Pokhara, budget 50000 NPR";
            var plan = @"
                Day 1: Arrive in Pokhara from Kathmandu by tourist bus.
                Day 2: Family-friendly activities around Phewa Lake.
                Day 3: Visit World Peace Pagoda, return to Kathmandu.
                Budget Total: 45000 NPR. Great for families with children.
            ";

            // Act
            var result = _sut.CheckRelevance(request, plan);

            // Assert
            result.Should().Contain("RELEVANT");
            result.Should().Contain("pokhara");
        }

        [Fact]
        public void CheckRelevance_WhenDestinationMissing_ShouldFlagIssue()
        {
            // Arrange
            var request = "3-day trip to Pokhara";
            var plan = "Day 1: Visit Kathmandu temples. Day 2: Bhaktapur tour. Day 3: Patan.";

            // Act
            var result = _sut.CheckRelevance(request, plan);

            // Assert
            result.Should().Contain("❌");
            result.Should().Contain("pokhara");
        }

        [Fact]
        public void CheckRelevance_WhenDurationMatches_ShouldPass()
        {
            // Arrange
            var request = "2-day trip to Pokhara";
            var plan = "Day 1: Arrive in Pokhara. Day 2: Explore Pokhara lakeside.";

            // Act
            var result = _sut.CheckRelevance(request, plan);

            // Assert
            result.Should().Contain("✅");
            result.Should().Contain("2 days");
        }

        [Fact]
        public void CheckRelevance_WhenTravelStyleMentioned_ShouldCheckIt()
        {
            // Arrange
            var request = "luxury trip to Pokhara";
            var plan = "Stay at a luxury 5-star resort in Pokhara with premium dining.";

            // Act
            var result = _sut.CheckRelevance(request, plan);

            // Assert
            result.Should().Contain("✅");
            result.Should().Contain("luxury");
        }

        [Fact]
        public void CheckRelevance_WhenTravelStyleMismatch_ShouldFlag()
        {
            // Arrange
            var request = "luxury trip to Pokhara";
            var plan = "Stay at a cheap hostel in Pokhara. Eat street food.";

            // Act
            var result = _sut.CheckRelevance(request, plan);

            // Assert
            result.Should().Contain("❌");
            result.Should().Contain("luxury");
        }

        [Fact]
        public void CheckRelevance_WithEmptyInput_ShouldReturnError()
        {
            var result = _sut.CheckRelevance("", "some plan");
            result.Should().Contain("VALIDATION ERROR");
        }

        [Theory]
        [InlineData("family trip to Pokhara", "Family activities in Pokhara with children", true)]
        [InlineData("solo adventure to Pokhara", "Solo trekking adventure around Pokhara", true)]
        [InlineData("honeymoon in Pokhara", "Romantic couple getaway in Pokhara", true)]
        public void CheckRelevance_StyleKeywords_ShouldDetectCorrectly(
            string request, string plan, bool shouldPass)
        {
            var result = _sut.CheckRelevance(request, plan);

            if (shouldPass)
                result.Should().Contain("RELEVANT");
        }

        #endregion

        #region CheckCompleteness Tests

        [Fact]
        public void CheckCompleteness_WhenAllSectionsPresent_ShouldBeComprehensive()
        {
            // Arrange
            var plan = @"
                Day 1: Morning flight to Pokhara. Check into Hotel Barahi.
                Day 2: Visit World Peace Pagoda by taxi.
                Budget total: 45000 NPR. Hotel cost 3000/night.
                Breakfast at lakeside cafe. Lunch at local restaurant. Dinner at Moondance.
                Safety: Emergency police 100. Tourist police 1144.
                Weather: 24°C partly cloudy.
            ";

            // Act
            var result = _sut.CheckCompleteness(plan, 2);

            // Assert
            var isGoodScore = result.Contains("COMPREHENSIVE") || result.Contains("ADEQUATE");
            isGoodScore.Should().BeTrue("Plan with all sections should be comprehensive or adequate");
        }

        [Fact]
        public void CheckCompleteness_WhenBudgetMissing_ShouldFlagIt()
        {
            // Arrange
            var plan = @"
                Day 1: Arrive in Pokhara. Check into hotel.
                Day 2: Visit temples by taxi.
            ";

            // Act
            var result = _sut.CheckCompleteness(plan, 2);

            // Assert
            result.Should().Contain("❌").And.Contain("Budget");
        }

        [Fact]
        public void CheckCompleteness_WhenDaysCovered_ShouldShowCorrectCount()
        {
            // Arrange
            var plan = "Day 1: Arrive. Day 2: Explore. Day 3: Return. Budget: 5000 NPR. Hotel Lakeside. Bus from Kathmandu.";

            // Act
            var result = _sut.CheckCompleteness(plan, 3);

            // Assert
            result.Should().Contain("3 days");
            result.Should().Contain("All days covered");
        }

        [Fact]
        public void CheckCompleteness_WhenDaysMissing_ShouldPenalize()
        {
            // Arrange
            var plan = "Day 1: Arrive. Budget: 5000 NPR. Hotel stay. Bus transport.";

            // Act
            var result = _sut.CheckCompleteness(plan, 3);

            // Assert
            result.Should().Contain("Missing");
        }

        [Fact]
        public void CheckCompleteness_WithEmptyInput_ShouldReturnError()
        {
            var result = _sut.CheckCompleteness("", 3);
            result.Should().Contain("VALIDATION ERROR");
        }

        [Fact]
        public void CheckCompleteness_WithOnlyItinerary_ShouldBeIncomplete()
        {
            // Arrange - only has itinerary, missing budget/accommodation/transport
            var plan = "Day 1: Morning sightseeing. Afternoon free time. Evening dinner.";

            // Act
            var result = _sut.CheckCompleteness(plan, 1);

            // Assert
            result.Should().Contain("❌");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public void CheckCompleteness_VariousDurations_ShouldCheckDayCoverage(int days)
        {
            // Arrange
            var planBuilder = new System.Text.StringBuilder();
            for (int d = 1; d <= days; d++)
            {
                planBuilder.AppendLine($"Day {d}: Activities for day {d}.");
            }
            planBuilder.AppendLine("Budget: 50000 NPR. Hotel Lakeside. Bus transport. Lunch at cafe.");

            // Act
            var result = _sut.CheckCompleteness(planBuilder.ToString(), days);

            // Assert
            result.Should().Contain("All days covered");
            result.Should().Contain($"{days} days");
        }

        #endregion
    }
}
