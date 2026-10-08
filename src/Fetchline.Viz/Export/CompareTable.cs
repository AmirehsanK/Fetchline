using System.Globalization;
using System.Text;
using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Export;

/// <summary>
/// One program under several configurations, as a table of plain text: a row for each way of
/// building the pipeline, with its cycles, its CPI and what the cycles were lost to. A row that
/// computed something else than the reference machine is marked, and the first wrong value is
/// named under the table.
/// </summary>
public static class CompareTable
{
    // The first three columns are names and read from the left; the rest are numbers.
    private const int Names = 3;

    public static string Write(Program program, IReadOnlyList<ComparisonRow> rows, IMessages messages)
    {
        var text = new StringBuilder();
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        string[] headings =
        [
            messages.HazardsHeading, messages.BranchHeading, messages.PredictorHeading, messages.CyclesHeading,
            messages.CpiHeading, messages.StallsHeading, messages.SquashedHeading, messages.WrongGuessesHeading,
        ];
        var cells = rows.Select(row => Cells(row, messages)).ToList();
        var widths = Enumerable.Range(0, headings.Length)
            .Select(column => Math.Max(headings[column].Length, cells.Max(row => row[column].Length)))
            .ToArray();

        // A gap between one way of handling hazards and the next, when each has several rows.
        var grouped = rows.Select(row => row.Config.Hazards).Distinct().Count() < rows.Count;

        WriteLine(text, headings, widths, note: string.Empty);
        for (var i = 0; i < rows.Count; i++)
        {
            if (grouped && i > 0 && rows[i].Config.Hazards != rows[i - 1].Config.Hazards)
            {
                text.Append('\n');
            }

            WriteLine(text, cells[i], widths, Note(rows[i], messages));
        }

        // What "the right answer" means needs a run that got to an end of its own.
        var right = rows
            .Where(row => row is { IsRight: true, Ended: true, Stopped: not StopReason.Fault })
            .MinBy(row => row.Stats.Cycles);
        var wrong = rows.FirstOrDefault(row => !row.IsRight);
        if ((right is not null && rows.Count > 1) || wrong is not null)
        {
            text.Append('\n');
        }

        if (right is not null && rows.Count > 1)
        {
            text.Append(' ')
                .Append(messages.FewestCycles(right.Stats.Instructions, SwitchNames.Of(right.Config), right.Stats.Cycles))
                .Append('\n');
        }

        if (wrong is not null)
        {
            var explainer = new Explainer(StaircaseLayout.Build([]), new InstructionLabels(program), messages);
            text.Append(' ')
                .Append(messages.WrongWith(SwitchNames.Of(wrong.Config), explainer.Describe(wrong.Divergence!)))
                .Append('\n');
        }

        return text.ToString();
    }

    private static string[] Cells(ComparisonRow row, IMessages messages)
    {
        var (config, stats) = (row.Config, row.Stats);
        return
        [
            SwitchNames.Of(config.Hazards),
            SwitchNames.Of(config.Branches),
            SwitchNames.Of(config.Predictor),
            stats.Cycles.ToString(CultureInfo.InvariantCulture),
            stats.Cpi.ToString("0.00", CultureInfo.InvariantCulture),
            stats.Stalls.ToString(CultureInfo.InvariantCulture),
            stats.Squashed.ToString(CultureInfo.InvariantCulture),
            messages.Guesses(stats.Mispredictions, stats.Branches),
        ];
    }

    /// <summary>What is said beside a row that is not simply a finished, correct run.</summary>
    private static string Note(ComparisonRow row, IMessages messages)
    {
        var notes = new List<string>(2);
        if (!row.IsRight)
        {
            notes.Add(messages.WrongAnswer);
        }

        if (!row.Ended)
        {
            notes.Add(messages.CutOff(row.Stats.Cycles));
        }

        return string.Join(", ", notes);
    }

    private static void WriteLine(StringBuilder text, string[] cells, int[] widths, string note)
    {
        var line = new StringBuilder();
        for (var column = 0; column < cells.Length; column++)
        {
            line.Append(column == 0 ? " " : "  ");
            line.Append(column < Names ? cells[column].PadRight(widths[column]) : cells[column].PadLeft(widths[column]));
        }

        if (note.Length > 0)
        {
            line.Append("  ").Append(note);
        }

        text.Append(line.ToString().TrimEnd()).Append('\n');
    }
}
