using System.Globalization;
using System.Text;
using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Export;

/// <summary>Which part of a run a trace shows.</summary>
public sealed record TraceOptions
{
    /// <summary>The first cycle of the diagram.</summary>
    public ulong From { get; init; } = 1;

    /// <summary>How many cycles the diagram covers; zero for all of them.</summary>
    public ulong Cycles { get; init; } = 40;

    /// <summary>Whether the hazard log is printed under the diagram.</summary>
    public bool Log { get; init; } = true;
}

/// <summary>
/// A run as plain text: the staircase diagram, the hazard log under it, and the totals. The
/// playground's diagram is the same grid of characters, so the terminal and the browser show
/// one picture, and this text is what the golden tests compare.
/// </summary>
public static class AsciiTrace
{
    /// <summary>What marks the cycle after an instruction was thrown away.</summary>
    public const string SquashedMark = "--";

    /// <param name="divergence">
    /// The first instruction on which the pipeline differed from the reference machine, when the
    /// run was checked and they did differ. It is reported under the totals.
    /// </param>
    public static string Write(
        Program program, IReadOnlyList<CycleRecord> records, IMessages messages, TraceOptions? options = null,
        Divergence? divergence = null)
    {
        options ??= new TraceOptions();
        var layout = StaircaseLayout.Build(records);
        var labels = new InstructionLabels(program);
        var explainer = new Explainer(layout, labels, messages);
        var text = new StringBuilder();

        var from = Math.Max(1, options.From);
        var to = options.Cycles == 0 ? layout.Cycles : Math.Min(layout.Cycles, from + options.Cycles - 1);

        // The diagram is the grid the playground draws, without the arrows it adds for forwards:
        // one piece of code decides where every character goes, here and there.
        var grid = StaircaseGrid.Build(layout, records, labels, from, to, arrows: false);
        var sawSquash = grid.HasSquashed;
        if (grid.Rows.Count > 0)
        {
            text.Append(grid.ToText()).Append('\n');
        }

        if (options.Log)
        {
            var lines = explainer.Explain(records).Where(line => line.Cycle >= from && line.Cycle <= to).ToList();
            if (lines.Count > 0)
            {
                var cycleWidth = 1 + lines.Max(line => line.Cycle).ToString(CultureInfo.InvariantCulture).Length;
                foreach (var line in lines)
                {
                    var cycle = "c" + line.Cycle.ToString(CultureInfo.InvariantCulture);
                    text.Append(' ').Append(cycle.PadRight(cycleWidth)).Append("  ")
                        .Append(explainer.Label(line.Kind).PadRight(8)).Append(' ')
                        .Append(line.Text).Append('\n');
                }
            }
        }

        if (sawSquash)
        {
            text.Append(' ').Append(messages.SquashedLegend(SquashedMark)).Append('\n');
        }

        if (from > 1 || to < layout.Cycles)
        {
            text.Append(' ').Append(messages.MoreCycles(from, to, layout.Cycles)).Append('\n');
        }

        var stats = PipelineStats.Of(records);
        text.Append(' ')
            .Append(messages.Summary(stats.Instructions, stats.Cycles, stats.Cpi, stats.Stalls, stats.Forwards, stats.Flushes))
            .Append('\n');

        foreach (var kind in Enum.GetValues<CacheKind>())
        {
            if (stats.CacheAccesses(kind) > 0)
            {
                text.Append(' ')
                    .Append(messages.CacheSummary(messages.NameOf(kind), stats.CacheAccesses(kind), stats.CacheMisses(kind)))
                    .Append('\n');
            }
        }

        if (divergence is not null)
        {
            text.Append(' ').Append(explainer.Describe(divergence)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The short name of a stage, as the textbooks write it.</summary>
    public static string Name(Stage stage) => stage switch
    {
        Stage.Fetch => "IF",
        Stage.Decode => "ID",
        Stage.Execute => "EX",
        Stage.Memory => "MEM",
        _ => "WB",
    };
}
