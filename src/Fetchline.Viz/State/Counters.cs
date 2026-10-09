using System.Globalization;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Explain;

namespace Fetchline.Viz.State;

/// <summary>One line of the counters pane.</summary>
/// <param name="Part">
/// It is a part of the counter above it: one cause among the stalls, one path among the forwards.
/// </param>
public readonly record struct Counter(string Label, string Value, bool Part = false);

/// <summary>
/// The counters of a run as lines of a label and a value: cycles and CPI, then what the cycles
/// were lost to and by what cause, what was forwarded and from where, and how the guesses about
/// branches came out. A cause that has not happened is left out, so a short run has a short list.
/// </summary>
public static class Counters
{
    public static IReadOnlyList<Counter> Of(PipelineStats stats, IMessages messages)
    {
        var lines = new List<Counter>
        {
            new(messages.CyclesHeading, Number(stats.Cycles)),
            new(messages.InstructionsHeading, Number(stats.Instructions)),
            new(messages.CpiHeading, stats.Cpi.ToString("0.00", CultureInfo.InvariantCulture)),
            new(messages.StallsHeading, Number(stats.Stalls)),
        };

        foreach (var cause in Enum.GetValues<StallCause>())
        {
            Part(lines, messages.NameOf(cause), stats.StallsBy(cause));
        }

        lines.Add(new Counter(messages.FlushesHeading, Number(stats.Flushes)));
        foreach (var cause in Enum.GetValues<FlushCause>())
        {
            Part(lines, messages.NameOf(cause), stats.FlushesBy(cause));
        }

        lines.Add(new Counter(messages.SquashedHeading, Number(stats.Squashed)));
        lines.Add(new Counter(messages.ForwardsHeading, Number(stats.Forwards)));
        Part(lines, "EX/MEM", stats.ForwardsFrom(ForwardSource.ExMem));
        Part(lines, "MEM/WB", stats.ForwardsFrom(ForwardSource.MemWb));

        lines.Add(new Counter(messages.BranchesHeading, Number(stats.Branches)));
        Part(lines, messages.TakenHeading, stats.BranchesTaken);
        Part(lines, messages.WrongGuessesHeading, stats.Mispredictions);

        // A cache that was never asked is one the pipeline does not have.
        foreach (var kind in Enum.GetValues<CacheKind>())
        {
            if (stats.CacheAccesses(kind) > 0)
            {
                lines.Add(new Counter(
                    messages.CacheMissesHeading(kind), Number(stats.CacheMisses(kind)) + "/" + Number(stats.CacheAccesses(kind))));
            }
        }

        lines.Add(new Counter(messages.LoadsHeading, Number(stats.Loads)));
        lines.Add(new Counter(messages.StoresHeading, Number(stats.Stores)));
        if (stats.Traps > 0)
        {
            lines.Add(new Counter(messages.TrapsHeading, Number(stats.Traps)));
        }

        return lines;
    }

    private static void Part(List<Counter> lines, string label, int count)
    {
        if (count > 0)
        {
            lines.Add(new Counter(label, Number(count), Part: true));
        }
    }

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
