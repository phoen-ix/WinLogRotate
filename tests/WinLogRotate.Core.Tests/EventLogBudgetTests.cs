using Shouldly;
using WinLogRotate.Hosting.Diagnostics;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// How many events one stream may put in the Application log, and what a caller is told.
/// </summary>
/// <remarks>
/// <para>
/// None of this could be tested where it used to live. The counting sat inside
/// <c>EventLogWriter</c>, which is Windows-annotated and returns before the counting is consulted
/// unless an event source is registered - and no source is registered on either CI leg. A test
/// that called <c>TryWrite</c> sixty times and asserted the fifty-first returned false would have
/// passed on both, having measured the absence of advapi32.
/// </para>
/// <para>
/// So the decision moved out, with no platform annotation and its state on an instance, and this
/// runs everywhere.
/// </para>
/// </remarks>
public sealed class EventLogBudgetTests
{
    /// <summary>
    /// An announcement is never the caller's answer.
    /// </summary>
    /// <remarks>
    /// The defect, stated as a rule. When the allowance ran out the writer returned the result of
    /// writing the <i>announcement</i> - a different event, under a different id - as though it
    /// were the caller's own. The notification digest read that as delivery, advanced the job's
    /// state, and the incident was recorded as reported without anything having been written.
    /// <para>
    /// Asserted over the whole enum rather than for the one value, so a verdict added later cannot
    /// quietly default to success.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAnnouncementIsNeverTheCallersAnswer(bool reported)
    {
        EventLogOutcomes.For(EventLogBudgetVerdict.Announce, reported)
            .ShouldBe(EventLogOutcome.OverBudget);

        EventLogOutcomes.For(EventLogBudgetVerdict.Drop, reported)
            .ShouldBe(EventLogOutcome.OverBudget);

        // And the one verdict that may be a success still depends on the write going.
        EventLogOutcomes.For(EventLogBudgetVerdict.Write, reported)
            .ShouldBe(reported ? EventLogOutcome.Written : EventLogOutcome.Refused);
    }

    /// <summary>Every verdict has an answer, so none can fall through to a default.</summary>
    [Fact]
    public void EveryVerdictIsAccountedFor()
    {
        var verdicts = Enum.GetValues<EventLogBudgetVerdict>();

        verdicts.Length.ShouldBeGreaterThan(2, "the three-valued verdict is the point of the type");

        foreach (var verdict in verdicts)
        {
            Should.NotThrow(() => EventLogOutcomes.For(verdict, reported: true));
        }
    }

    /// <summary>
    /// The allowance is spent, announced once, and then silent.
    /// </summary>
    [Fact]
    public void TheAllowanceIsAnnouncedExactlyOnce()
    {
        var budget = EventLogBudget.ForDiagnostics();

        for (var i = 0; i < EventLogBudget.MaxDiagnosticsPerRun; i++)
        {
            budget.Take().ShouldBe(EventLogBudgetVerdict.Write, $"event {i + 1} is inside the allowance");
        }

        budget.Take().ShouldBe(EventLogBudgetVerdict.Announce);
        budget.Take().ShouldBe(EventLogBudgetVerdict.Drop);
        budget.Take().ShouldBe(EventLogBudgetVerdict.Drop);
    }

    /// <summary>
    /// A flood of diagnostics does not spend the digests' allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion that discriminates, and the reason the two streams are two. Every other fact
    /// here is satisfiable by a single counter; this one is a claim about two.
    /// </para>
    /// <para>
    /// It is also the scenario the defect needed. The engine mirrors a diagnostic per failing
    /// file, and <c>RunCommand</c> runs the notification phase last - so on a night bad enough to
    /// produce fifty diagnostics the digest arrived fifty-first, which is exactly the night it
    /// existed for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFloodOfDiagnosticsDoesNotSpendTheDigestsAllowance()
    {
        var diagnostics = EventLogBudget.ForDiagnostics();
        var digests = EventLogBudget.ForDigests();

        for (var i = 0; i < EventLogBudget.MaxDiagnosticsPerRun + 10; i++)
        {
            diagnostics.Take();
        }

        diagnostics.Take().ShouldBe(EventLogBudgetVerdict.Drop, "the diagnostics really are spent");
        digests.Take().ShouldBe(EventLogBudgetVerdict.Write);
    }

    /// <summary>
    /// The digests have no count ceiling at all.
    /// </summary>
    /// <remarks>
    /// One digest per job plus one for the run, so the number is bounded by the configuration
    /// somebody wrote rather than by how badly the night went. Giving it the diagnostics' fifty
    /// would silently drop the tail on an install with more jobs than that - this milestone's own
    /// defect, at a different scale. Asserted well past the other allowance so the two cannot
    /// quietly become one number again.
    /// </remarks>
    [Fact]
    public void TheDigestsHaveNoCeiling()
    {
        var digests = EventLogBudget.ForDigests();

        for (var i = 0; i < (EventLogBudget.MaxDiagnosticsPerRun * 4) + 1; i++)
        {
            digests.Take().ShouldBe(EventLogBudgetVerdict.Write, $"digest {i + 1} must still be written");
        }
    }
}
