using Fetchline.Viz.Explain;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Export;

/// <summary>What a run can be saved as.</summary>
public enum ExportKind : byte
{
    /// <summary>Every cycle, for a program to read.</summary>
    Json,

    /// <summary>Every cycle, as a log for the pipeline viewer Konata.</summary>
    Kanata,

    /// <summary>The diagram, as a picture made of lines and text.</summary>
    Svg,

    /// <summary>The diagram, as a picture made of dots: the same SVG, for the page to turn into one.</summary>
    Png,
}

/// <summary>A file to hand to the reader: its name, what kind of thing it is, and what is in it.</summary>
public sealed record ExportFile(string Name, string MediaType, string Text);

/// <summary>
/// What the playground saves when asked: the run as far as the cycle on screen. A trace holds
/// every cycle there is still a record of. A picture holds the last cycles up to the one on
/// screen, because a picture of twenty thousand cycles is not one anybody can look at.
/// </summary>
public static class SessionExport
{
    /// <summary>The most cycles a picture of the diagram covers.</summary>
    public const ulong MostCyclesDrawn = 120;

    /// <summary>The file, or null when there is nothing to save: no program, or no cycle run yet.</summary>
    public static ExportFile? Make(Session session, ExportKind kind, IMessages messages)
    {
        if (session.Program is not { } program || session.Cycle == 0)
        {
            return null;
        }

        switch (kind)
        {
            case ExportKind.Json:
                return new ExportFile(
                    "fetchline-trace.json", "application/json",
                    JsonTrace.Write(program, session.Config, session.Records(session.FirstKept, session.Cycle)));
            case ExportKind.Kanata:
                return new ExportFile(
                    "fetchline-trace.kanata.log", "text/plain",
                    KanataTrace.Write(program, session.Records(session.FirstKept, session.Cycle)));
            default:
                var from = Math.Max(session.FirstKept, session.Cycle - Math.Min(session.Cycle, MostCyclesDrawn) + 1);
                var grid = StaircaseGrid.Build(session.Records(from, session.Cycle), new InstructionLabels(program), from, session.Cycle);
                var picture = StaircaseSvg.Write(grid, messages.PipelineTitle);
                return kind == ExportKind.Svg
                    ? new ExportFile("fetchline-diagram.svg", "image/svg+xml", picture)
                    : new ExportFile("fetchline-diagram.png", "image/png", picture);
        }
    }
}
