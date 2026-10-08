using Fetchline.Core.Pipeline;
using Fetchline.Viz;
using Fetchline.Viz.Explain;

namespace Fetchline.Tests.Viz;

/// <summary>The hazard log the playground lists, and that any stretch of a run can be explained alone.</summary>
public class HazardLogTests
{
    private static readonly IMessages Messages = EnglishMessages.Instance;

    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    private static Session Finished(string source, PipelineConfig? config = null)
    {
        var session = new Session(source, config);
        session.Seek(ulong.MaxValue);
        return session;
    }

    private static List<LogLine> Log(Session session, ulong from, ulong to) => HazardLog.Build(
        session.Recorded(from, to), new InstructionLabels(session.Program!), Messages, session.Divergence);

    [Fact]
    public void TheLoadUseSequenceHasItsThreeLines()
    {
        var session = Finished(Example("load-use.s"));

        Assert.Equal(
            [
                new LogLine(3, LogKind.Stall, "load-use: add (ID) needs x4; lw (EX) has it only after MEM", Seq: 2, Other: 1),
                new LogLine(5, LogKind.Forward, "MEM/WB -> EX.A   x4 from lw", Seq: 2, Other: 1),
                new LogLine(6, LogKind.Forward, "EX/MEM -> EX.A   x5 from add", Seq: 3, Other: 2),
            ],
            Log(session, 1, session.Cycle));
    }

    [Fact]
    public void AnyStretchOfARunIsExplainedTheSameOnItsOwn()
    {
        // The lines of cycles 20 to 30 are the same whether the log is made from the whole run
        // or from those eleven records alone: every instruction a line names was in the
        // pipeline in the cycle of the line. And no name is ever missing.
        foreach (var example in new[] { "bubble-sort.s", "factorial.s", "sum.s", "gcd.s", "fib.s", "multiply.s" })
        {
            foreach (var config in new[]
            {
                PipelineConfig.Default,
                new PipelineConfig { Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, MulDivCycles = 3 },
                new PipelineConfig { Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit },
            })
            {
                var session = Finished(Example(example), config);
                var whole = Log(session, 1, session.Cycle);
                Assert.All(whole, line => Assert.DoesNotContain("?", line.Text));

                for (ulong from = 1; from <= session.Cycle; from += 7)
                {
                    var to = Math.Min(session.Cycle, from + 10);
                    Assert.Equal(
                        whole.Where(line => line.Cycle >= from && line.Cycle <= to),
                        Log(session, from, to));
                }
            }
        }
    }

    [Fact]
    public void TheLinesAreInTheOrderThingsHappened()
    {
        var lines = Log(Finished(Example("bubble-sort.s")), 1, ulong.MaxValue);

        Assert.True(lines.Count > 100);
        Assert.Equal(lines.OrderBy(line => line.Cycle).Select(line => line.Cycle), lines.Select(line => line.Cycle));
        Assert.Contains(lines, line => line.Kind == LogKind.Stall);
        Assert.Contains(lines, line => line.Kind == LogKind.Forward);
        Assert.Contains(lines, line => line.Kind == LogKind.Flush);
    }

    [Fact]
    public void TheFirstWrongValueIsALineInTheCycleItHappened()
    {
        var session = Finished(Example("sum.s"), new PipelineConfig { Hazards = HazardHandling.Off });
        var lines = Log(session, 1, session.Cycle);

        var wrong = Assert.Single(lines, line => line.Kind == LogKind.Wrong);
        Assert.Equal(8ul, wrong.Cycle);
        Assert.Equal(
            "first wrong value: instruction 4, 'add a0, a0, t0', in cycle 8: " +
            "the pipeline wrote a0 = 0x00000000, the reference machine wrote a0 = 0x00000001",
            wrong.Text);

        // It is not said again in a stretch that does not hold its cycle.
        Assert.DoesNotContain(Log(session, 9, session.Cycle), line => line.Kind == LogKind.Wrong);
        Assert.Contains(Log(session, 8, 8), line => line.Kind == LogKind.Wrong);
    }

    [Fact]
    public void TheLogCanReachPastTheCycleOnScreen()
    {
        // Stepped back to cycle 4, the session still has what happened after it, so the log can
        // list it and offer the way forward again.
        var session = Finished(Example("load-use.s"));
        session.Seek(4);

        Assert.Equal([3ul], Log(session, 1, session.Cycle).Select(line => line.Cycle));
        Assert.Equal([3ul, 5ul, 6ul], Log(session, 1, session.Frontier).Select(line => line.Cycle));
        Assert.Equal(4, session.Records(1, 99).Count);
        Assert.Equal(8, session.Recorded(1, 99).Count);
        Assert.Empty(session.Recorded(9, 99));
        Assert.Empty(new Session("nop").Recorded(1, 5));
    }

    [Fact]
    public void EveryKindOfLineHasItsWord()
    {
        var explainer = new Explainer(
            Fetchline.Viz.Staircase.StaircaseLayout.Build([]),
            new InstructionLabels(new Session("nop").Program!),
            Messages);

        Assert.Equal(["stall", "forward", "flush", "trap", "wrong"], Enum.GetValues<LogKind>().Select(explainer.Label));
    }
}
