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

    private const int Gap = 5;

    public static string Write(
        Program program, IReadOnlyList<CycleRecord> records, IMessages messages, TraceOptions? options = null)
    {
        options ??= new TraceOptions();
        var layout = StaircaseLayout.Build(records);
        var labels = new InstructionLabels(program);
        var text = new StringBuilder();

        var from = Math.Max(1, options.From);
        var to = options.Cycles == 0 ? layout.Cycles : Math.Min(layout.Cycles, from + options.Cycles - 1);

        // A row is shown if any of its cells, or the mark of its being squashed, is in the window.
        var rows = layout.Rows
            .Where(row => row.FirstCycle <= to && row.LastCycle + (row.WasSquashed ? 1ul : 0) >= from)
            .Select(row => (Row: row, Text: labels.Describe(row.Pc, row.Raw)))
            .ToList();

        var sawSquash = false;
        if (rows.Count > 0)
        {
            var addressWidth = Math.Max(2, rows.Max(row => row.Row.Pc.ToString("x", CultureInfo.InvariantCulture).Length));
            var mnemonicWidth = Math.Max(5, rows.Max(row => row.Text.Mnemonic.Length) + 1);
            var textWidth = rows.Max(row => row.Text.Operands.Length == 0
                ? row.Text.Mnemonic.Length
                : mnemonicWidth + row.Text.Operands.Length);
            var cellWidth = Math.Max(5, to.ToString(CultureInfo.InvariantCulture).Length + 1);
            var margin = 1 + 2 + addressWidth + 1 + textWidth + Gap;

            var header = new StringBuilder().Append(' ', margin);
            for (var cycle = from; cycle <= to; cycle++)
            {
                header.Append(cycle.ToString(CultureInfo.InvariantCulture).PadRight(cellWidth));
            }

            text.Append(header.ToString().TrimEnd()).Append('\n');

            foreach (var (row, description) in rows)
            {
                var line = new StringBuilder(" 0x")
                    .Append(row.Pc.ToString("x", CultureInfo.InvariantCulture).PadLeft(addressWidth, '0'))
                    .Append(' ')
                    .Append(description.Operands.Length == 0
                        ? description.Mnemonic
                        : description.Mnemonic.PadRight(mnemonicWidth) + description.Operands);
                line.Append(' ', margin - line.Length);

                for (var cycle = from; cycle <= to; cycle++)
                {
                    var cell = row.At(cycle) is { } occupied ? Name(occupied.Stage)
                        : row.WasSquashed && cycle == row.LastCycle + 1 ? SquashedMark
                        : string.Empty;
                    sawSquash |= cell == SquashedMark;
                    line.Append(cell.PadRight(cellWidth));
                }

                text.Append(line.ToString().TrimEnd()).Append('\n');
            }

            text.Append('\n');
        }

        if (options.Log)
        {
            var explainer = new Explainer(layout, labels, messages);
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
