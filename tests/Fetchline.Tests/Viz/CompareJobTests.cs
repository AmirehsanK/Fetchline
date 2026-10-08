using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;

namespace Fetchline.Tests.Viz;

/// <summary>
/// The comparison as the playground makes it: a stretch at a time, so the page can be drawn in
/// between, and a line at a time, so a row can be chosen.
/// </summary>
public class CompareJobTests
{
    private static readonly IMessages Messages = EnglishMessages.Instance;

    private static Fetchline.Core.Asm.Program Example(string name) =>
        AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", name)));

    [Theory]
    [InlineData(1ul)]
    [InlineData(7ul)]
    [InlineData(100ul)]
    [InlineData(1_000_000ul)]
    public void MadeAStretchAtATimeItIsTheTableMadeAllAtOnce(ulong stretch)
    {
        var program = Example("sum.s");
        var configurations = Comparison.Configurations();
        var atOnce = configurations.Select(config => Comparison.Run(program, config, 10_000)).ToList();

        var job = new CompareJob(program, configurations, 10_000);
        var calls = 0;
        while (job.Advance(stretch))
        {
            Assert.True(++calls < 100_000);
        }

        Assert.True(job.IsDone);
        Assert.Null(job.Running);
        Assert.Equal(24, job.Rows.Count);
        Assert.Equal(CompareTable.Write(program, atOnce, Messages), CompareTable.Write(program, job.Rows, Messages));
        Assert.False(job.Advance(stretch));         // nothing left to do, however often it is asked
    }

    [Fact]
    public void TheRowsArriveOneByOneInTheOrderOfTheTable()
    {
        var program = Example("sum.s");
        var configurations = Comparison.Configurations();
        var job = new CompareJob(program, configurations, 10_000);

        Assert.Equal((24, 0, false), (job.Total, job.Rows.Count, job.IsDone));
        Assert.Null(job.Running);

        // Thirty cycles into a run of sixty-eight: no row yet, and the first is under way.
        Assert.True(job.Advance(30));
        Assert.Empty(job.Rows);
        Assert.Equal((PipelineConfig.Default, 30ul), (job.Running!.Config, job.Running.Stats.Cycles));

        // Enough for the rest of it and some of the next.
        Assert.True(job.Advance(60));
        var first = Assert.Single(job.Rows);
        Assert.Equal((PipelineConfig.Default, 68ul, StopReason.Exit), (first.Config, first.Stats.Cycles, first.Stopped));
        Assert.Equal((configurations[1], 22ul), (job.Running!.Config, job.Running.Stats.Cycles));

        while (job.Advance(500))
        {
        }

        Assert.Equal(configurations, job.Rows.Select(row => row.Config));
    }

    [Fact]
    public void AConfigurationThatGoesOnTooLongIsCutOffAndTheNextBegun()
    {
        var program = AssemblerTesting.Assemble("spin: j spin");
        var job = new CompareJob(program, Comparison.Configurations(hazards: [HazardHandling.Forwarding]), mostCycles: 50);

        while (job.Advance(17))
        {
        }

        Assert.Equal(8, job.Rows.Count);
        Assert.All(job.Rows, row => Assert.Equal((50ul, false, StopReason.None), (row.Stats.Cycles, row.Ended, row.Stopped)));
        Assert.Equal(50ul, job.MostCycles);
    }

    [Fact]
    public void ARunIsMadeInStretchesToo()
    {
        var run = new ComparisonRun(Example("load-use.s"), PipelineConfig.Default);

        Assert.Equal((0ul, false), (run.Cycles, run.HasEnded));
        Assert.Equal(3ul, run.Advance(3));
        Assert.Equal((3ul, 1), (run.Row.Stats.Cycles, run.Row.Stats.Stalls));
        Assert.False(run.HasEnded);

        // Asked for more than is left, it runs what is left and says how much that was.
        Assert.Equal(5ul, run.Advance(100));
        Assert.Equal((8ul, true, StopReason.EndOfProgram), (run.Cycles, run.HasEnded, run.Row.Stopped));
        Assert.Equal(0ul, run.Advance(100));
        Assert.True(run.Row.IsRight && run.Row.Ended);
    }

    [Fact]
    public void TheTableComesALineAtATimeWithTheRowEachLineIsAbout()
    {
        var program = Example("sum.s");
        var rows = Comparison.Configurations().Select(config => Comparison.Run(program, config, 10_000)).ToList();

        var lines = CompareTable.Lines(program, rows, Messages);

        // The headings, twenty-four rows in three groups with a gap between groups, a gap, and
        // the two lines under the table.
        Assert.Equal(1 + 24 + 2 + 1 + 2, lines.Count);
        Assert.Null(lines[0].Row);
        Assert.StartsWith(" hazards ", lines[0].Text);
        Assert.Equal(rows, lines.Where(line => line.Row is not null).Select(line => line.Row!));
        Assert.Equal(
            [9, 18, 27],
            lines.Select((line, index) => (line, index)).Where(pair => pair.line.Text.Length == 0).Select(pair => pair.index));
        Assert.Equal(" forwarding  ex      not-taken           68  1.70       0        23        9 of 10", lines[1].Text);
        Assert.StartsWith(" 40 instructions; fewest cycles", lines[^2].Text);
        Assert.StartsWith(" off, ex, not-taken: first wrong value", lines[^1].Text);

        // And the text of the table is those lines, one under another.
        Assert.Equal(string.Concat(lines.Select(line => line.Text + "\n")), CompareTable.Write(program, rows, Messages));
        Assert.Empty(CompareTable.Lines(program, [], Messages));
    }

    [Fact]
    public void ATableStillBeingMadeIsATableOfTheRowsThereAre()
    {
        var program = Example("sum.s");
        var job = new CompareJob(program, Comparison.Configurations(), 10_000);
        job.Advance(200);

        // Three rows are done. With no row yet that got the answer wrong, nothing is said of one.
        var lines = CompareTable.Lines(program, job.Rows, Messages);
        Assert.Equal(3, job.Rows.Count);
        Assert.Equal(1 + 3 + 1 + 1, lines.Count);
        Assert.StartsWith(" 40 instructions; fewest cycles with the right answer: forwarding, ex, backward-taken (52)", lines[^1].Text);
    }
}
