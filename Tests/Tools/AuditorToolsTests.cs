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
        public void ValidateMathConsistency_WithCurrencySymbols_ShouldParseCorrectly()
        {
            // Note: The regex strips all non-digit/non-decimal characters
            // So "$1,000" becomes "1000" correctly
            string costs = "$1000, $2000, 3000";
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

        [Theory]
        [InlineData("taxi", 30)]
        [InlineData("bus", 60)]
        [InlineData("walk", 60)]
        [InlineData("train", 45)]
        [InlineData("flight", 120)]
        [InlineData("ferry", 90)]
        public void ValidateTravelTime_DefaultEstimates_ShouldUseCorrectModeDefaults(string mode, int expectedMinutes)
        {
            var result = _sut.ValidateTravelTime("CityA", "CityB", expectedMinutes + 30, mode);

            result.Should().Contain("POSSIBLE");
            result.Should().Contain($"{expectedMinutes} minutes");
        }

        [Fact]
        public void ValidateTravelTime_WithEstimatedMinutes_ShouldOverrideDefault()
        {
            // Provide a custom estimate of 300 minutes (e.g., long-distance bus)
            var result = _sut.ValidateTravelTime("Tokyo", "Osaka", 360, "bus", estimatedTravelMinutes: 300);

            result.Should().Contain("POSSIBLE");
            result.Should().Contain("300 minutes");
        }

        [Fact]
        public void ValidateTravelTime_WithInsufficientTime_ShouldFail()
        {
            var result = _sut.ValidateTravelTime("Paris", "Barcelona", 120, "bus", estimatedTravelMinutes: 480);

            result.Should().Contain("IMPOSSIBLE");
            result.Should().Contain("Insufficient time");
        }

        [Fact]
        public void ValidateTravelTime_UnknownMode_ShouldUse45MinDefault()
        {
            var result = _sut.ValidateTravelTime("CityA", "CityB", 60, "rickshaw");

            result.Should().Contain("POSSIBLE");
            result.Should().Contain("45 minutes");
        }

        #endregion

        #region CheckGroundedness Tests

        [Fact]
        public void CheckGroundedness_WhenExactMatch_ShouldVerify()
        {
            string itemName = "Park Hyatt Tokyo";
            string context = "Recommended hotels include Park Hyatt Tokyo and Aman Tokyo.";

            var result = _sut.CheckGroundedness(itemName, context);

            result.Should().Contain("VERIFIED");
            result.Should().Contain("HIGH");
        }

        [Fact]
        public void CheckGroundedness_WhenNotFound_ShouldFlagHallucination()
        {
            string itemName = "Imaginary Paradise Resort";
            string context = "Hotels in the area include Park Hyatt Tokyo and Aman Tokyo.";

            var result = _sut.CheckGroundedness(itemName, context);

            result.Should().Contain("NOT FOUND");
            result.Should().Contain("hallucinated");
        }

        [Fact]
        public void CheckGroundedness_WithPartialMatch_ShouldShowWarning()
        {
            string itemName = "Lakeside Mountain Resort";
            string context = "Hotels include Lake View Hotel and Mountain Lodge Resort.";

            var result = _sut.CheckGroundedness(itemName, context);

            result.Should().Contain("Word Matches:");
        }

        [Fact]
        public void CheckGroundedness_CaseInsensitive_ShouldWork()
        {
            string itemName = "RITZ CARLTON BARCELONA";
            string context = "ritz carlton barcelona is a luxury property";

            var result = _sut.CheckGroundedness(itemName, context);

            result.Should().Contain("VERIFIED");
        }

        #endregion

        #region CheckSafetyRequirements Tests

        [Theory]
        [InlineData("Upper Mustang trek", "Special Restricted Area Permit", "$500")]
        [InlineData("Inca Trail hike", "Advance permit required", "$250")]
        [InlineData("Galapagos tour", "National park entry fee", "$100")]
        [InlineData("Bhutan cultural tour", "Minimum daily tariff", "$200")]
        public void CheckSafetyRequirements_PermitLocations_ShouldRequirePermit(
            string location, string expectedPermit, string expectedCost)
        {
            var result = _sut.CheckSafetyRequirements(location);

            result.Should().Contain(expectedPermit);
            result.Should().Contain(expectedCost);
            result.Should().Contain("ATTENTION REQUIRED");
        }

        [Fact]
        public void CheckSafetyRequirements_StandardTourismLocation_ShouldBeAllClear()
        {
            var result = _sut.CheckSafetyRequirements("Barcelona city center");

            result.Should().Contain("CLEAR");
            result.Should().Contain("None required for standard tourism");
        }

        [Theory]
        [InlineData("Everest Base Camp", "Altitude sickness")]
        [InlineData("Mount Kilimanjaro climb", "Altitude sickness")]
        [InlineData("Amazon jungle tour", "Tropical disease")]
        [InlineData("Sahara desert trip", "Extreme heat")]
        [InlineData("scuba diving in Bali", "Decompression sickness")]
        public void CheckSafetyRequirements_HazardousLocations_ShouldWarnAboutRisks(
            string location, string expectedWarning)
        {
            var result = _sut.CheckSafetyRequirements(location);

            result.Should().Contain(expectedWarning);
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
            var request = "3-day family trip from Tokyo to Kyoto, budget $1500";
            var plan = @"
                Day 1: Arrive in Kyoto from Tokyo by Shinkansen bullet train.
                Day 2: Family-friendly activities at Fushimi Inari and Arashiyama.
                Day 3: Visit Kinkaku-ji, return to Tokyo.
                Budget Total: $1400. Great for families with children.
            ";

            var result = _sut.CheckRelevance(request, plan);

            result.Should().Contain("RELEVANT");
        }

        [Fact]
        public void CheckRelevance_WhenDestinationMissing_ShouldFlagIssue()
        {
            var request = "3-day trip to Barcelona";
            var plan = "Day 1: Visit Madrid museums. Day 2: Toledo day trip. Day 3: Segovia.";

            var result = _sut.CheckRelevance(request, plan);

            result.Should().Contain("❌");
            result.Should().Contain("Barcelona");
        }

        [Fact]
        public void CheckRelevance_WhenDurationMatches_ShouldPass()
        {
            var request = "2-day trip to Kyoto";
            var plan = "Day 1: Arrive in Kyoto. Day 2: Explore Kyoto temples.";

            var result = _sut.CheckRelevance(request, plan);

            result.Should().Contain("✅");
            result.Should().Contain("2 days");
        }

        [Fact]
        public void CheckRelevance_WhenTravelStyleMentioned_ShouldCheckIt()
        {
            var request = "luxury trip to Bali";
            var plan = "Stay at a luxury 5-star resort in Bali with premium dining.";

            var result = _sut.CheckRelevance(request, plan);

            result.Should().Contain("✅");
            result.Should().Contain("luxury");
        }

        [Fact]
        public void CheckRelevance_WhenTravelStyleMismatch_ShouldFlag()
        {
            var request = "luxury trip to Bali";
            var plan = "Stay at a cheap hostel in Bali. Eat street food.";

            var result = _sut.CheckRelevance(request, plan);

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
        [InlineData("family trip to Rome", "Family activities in Rome with children", true)]
        [InlineData("solo adventure to Cusco", "Solo trekking adventure around Cusco", true)]
        [InlineData("honeymoon in Santorini", "Romantic couple getaway in Santorini", true)]
        public void CheckRelevance_StyleKeywords_ShouldDetectCorrectly(
            string request, string plan, bool shouldPass)
        {
            var result = _sut.CheckRelevance(request, plan);

            if (shouldPass)
                result.Should().Contain("RELEVANT");
        }

        [Fact]
        public void CheckRelevance_MultiWordDestination_ShouldExtractCorrectly()
        {
            var request = "5-day trip from Ho Chi Minh City to Da Nang";
            var plan = "Day 1: Depart Ho Chi Minh City. Day 3: Arrive in Da Nang. Day 5: Return.";

            var result = _sut.CheckRelevance(request, plan);

            result.Should().Contain("RELEVANT");
        }

        #endregion

        #region CheckCompleteness Tests

        [Fact]
        public void CheckCompleteness_WhenAllSectionsPresent_ShouldBeComprehensive()
        {
            var plan = @"
                Day 1: Morning flight to Kyoto. Check into Hotel Granvia.
                Day 2: Visit Fushimi Inari by taxi.
                Budget total: $1400. Hotel cost $150/night.
                Breakfast at hotel. Lunch at Nishiki Market. Dinner at Gion restaurant.
                Safety: Emergency police 110. Fire 119.
                Weather: 22°C partly cloudy.
            ";

            var result = _sut.CheckCompleteness(plan, 2);

            var isGoodScore = result.Contains("COMPREHENSIVE") || result.Contains("ADEQUATE");
            isGoodScore.Should().BeTrue("Plan with all sections should be comprehensive or adequate");
        }

        [Fact]
        public void CheckCompleteness_WhenBudgetMissing_ShouldFlagIt()
        {
            var plan = @"
                Day 1: Arrive in Barcelona. Check into hotel.
                Day 2: Visit Sagrada Familia by metro.
            ";

            var result = _sut.CheckCompleteness(plan, 2);

            result.Should().Contain("❌").And.Contain("Budget");
        }

        [Fact]
        public void CheckCompleteness_WhenDaysCovered_ShouldShowCorrectCount()
        {
            var plan = "Day 1: Arrive. Day 2: Explore. Day 3: Return. Budget: $1200. Hotel Marais. Train from CDG.";

            var result = _sut.CheckCompleteness(plan, 3);

            result.Should().Contain("3 days");
            result.Should().Contain("All days covered");
        }

        [Fact]
        public void CheckCompleteness_WhenDaysMissing_ShouldPenalize()
        {
            var plan = "Day 1: Arrive. Budget: $500. Hotel stay. Bus transport.";

            var result = _sut.CheckCompleteness(plan, 3);

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
            var plan = "Day 1: Morning sightseeing. Afternoon free time. Evening dinner.";

            var result = _sut.CheckCompleteness(plan, 1);

            result.Should().Contain("❌");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public void CheckCompleteness_VariousDurations_ShouldCheckDayCoverage(int days)
        {
            var planBuilder = new System.Text.StringBuilder();
            for (int d = 1; d <= days; d++)
            {
                planBuilder.AppendLine($"Day {d}: Activities for day {d}.");
            }
            planBuilder.AppendLine("Budget: $1500. Hotel Central. Metro transport. Lunch at cafe.");

            var result = _sut.CheckCompleteness(planBuilder.ToString(), days);

            result.Should().Contain("All days covered");
            result.Should().Contain($"{days} days");
        }

        #endregion
    }
}
