using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Extensions.AI;
using Moq;

namespace LocalAgentTravelPlanner.Tests.Conversations;

/// <summary>
/// Weak-model safety net coverage. Verifies the extractor grabs the Auditor's actual
/// decision (regardless of formatting variance) and that the enforcer rewrites the plan
/// header on mismatch. Prompt-level rules are suggestions; this class asserts the
/// programmatic backstop.
/// </summary>
public class EnforceAuditorVerdictTests
{
    private static TravelPlannerService BuildSut()
    {
        var chat = new Mock<IChatClient>();
        return new TravelPlannerService(
            chat.Object,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
    }

    [Theory]
    [InlineData("FINAL VERDICT: APPROVED", "APPROVED")]
    [InlineData("Decision: FLAGGED", "FLAGGED")]
    [InlineData("Status: REJECTED — cost exceeds budget", "REJECTED")]
    [InlineData("**Final Verdict:** APPROVED", "APPROVED")]
    [InlineData("The plan is APPROVED after review.", "APPROVED")]
    [InlineData("no verdict here", null)]
    [InlineData("", null)]
    public void ExtractAuditorVerdict_prioritizes_labeled_verdict_over_bare(string input, string? expected)
    {
        var actual = TravelPlannerService.ExtractAuditorVerdict(input);
        actual.Should().Be(expected);
    }

    [Fact]
    public void ExtractAuditorVerdict_prefers_labeled_when_both_present()
    {
        // Bare "REJECTED" appears earlier but the labeled "APPROVED" is authoritative.
        var raw = "Earlier the plan looked REJECTED. After revision — FINAL VERDICT: APPROVED";
        TravelPlannerService.ExtractAuditorVerdict(raw).Should().Be("APPROVED");
    }

    [Fact]
    public void EnforceAuditorVerdict_rewrites_plan_status_on_mismatch()
    {
        var sut = BuildSut();
        var plan = "# Trip\n\n### ✅ Plan Status: REJECTED ❌\n> ...";
        var auditor = "FINAL VERDICT: APPROVED";

        var result = sut.EnforceAuditorVerdict(plan, auditor, "conv-1");

        result.Should().Contain("Plan Status: APPROVED");
        result.Should().NotContain("Plan Status: REJECTED");
    }

    [Fact]
    public void EnforceAuditorVerdict_leaves_plan_untouched_when_verdicts_match()
    {
        var sut = BuildSut();
        var plan = "### ✅ Plan Status: APPROVED ✅\n> Score 4.5/5";
        var auditor = "Decision: APPROVED";

        var result = sut.EnforceAuditorVerdict(plan, auditor, "conv-1");

        result.Should().Be(plan);
    }

    [Fact]
    public void EnforceAuditorVerdict_returns_plan_when_auditor_output_missing()
    {
        var sut = BuildSut();
        var plan = "Plan Status: FLAGGED";

        sut.EnforceAuditorVerdict(plan, null, "conv-1").Should().Be(plan);
        sut.EnforceAuditorVerdict(plan, "", "conv-1").Should().Be(plan);
    }

    [Fact]
    public void EnforceAuditorVerdict_returns_plan_when_no_status_block_in_plan()
    {
        // No "Plan Status:" line to rewrite — enforcer leaves output alone rather than
        // guessing where to insert the verdict.
        var sut = BuildSut();
        var plan = "# Trip\nA nice trip.";
        var auditor = "FINAL VERDICT: REJECTED";

        sut.EnforceAuditorVerdict(plan, auditor, "conv-1").Should().Be(plan);
    }

    [Fact]
    public void SplitPlanAndChanges_extracts_change_summary_when_present()
    {
        var raw = "# Plan body\n\nBody content.\n\n## 🔄 Changes This Turn\n- Cut day 3 dinner\n- Total now $1400";

        var (plan, changes) = TravelPlannerService.SplitPlanAndChanges(raw);

        plan.Should().Be("# Plan body\n\nBody content.");
        changes.Should().Be("- Cut day 3 dinner\n- Total now $1400");
    }

    [Fact]
    public void SplitPlanAndChanges_returns_full_text_when_heading_absent()
    {
        var raw = "# Plan body\nno changes section";

        var (plan, changes) = TravelPlannerService.SplitPlanAndChanges(raw);

        plan.Should().Be(raw);
        changes.Should().BeNull();
    }

    [Theory]
    [InlineData("## Changes This Turn")]
    [InlineData("### 🔄 Changes This Turn")]
    [InlineData("### Changes This Turn")]
    public void SplitPlanAndChanges_accepts_heading_variants(string heading)
    {
        var raw = $"plan\n\n{heading}\n- one\n- two";

        var (plan, changes) = TravelPlannerService.SplitPlanAndChanges(raw);

        plan.Should().Be("plan");
        changes.Should().Be("- one\n- two");
    }
}
