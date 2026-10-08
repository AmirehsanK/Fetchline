using Fetchline.Core.Pipeline;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Explain;

/// <summary>
/// The hazard log of a stretch of a run: every stall, forward, flush and trap as a sentence, in
/// the order they happened, with the first wrong value among them if the run went wrong there.
///
/// Any stretch can be explained on its own. A sentence names instructions, and an instruction is
/// only known from the record of a cycle it was in; but whatever an event is about was in the
/// pipeline in the cycle of the event, so each record carries all the names its own lines need.
/// </summary>
public static class HazardLog
{
    /// <param name="records">The records of the cycles to explain.</param>
    /// <param name="divergence">The first wrong value of the run, if it has one.</param>
    public static List<LogLine> Build(
        IReadOnlyList<CycleRecord> records, InstructionLabels labels, IMessages messages, Divergence? divergence = null)
    {
        var explainer = new Explainer(StaircaseLayout.Build(records), labels, messages);
        var lines = new List<LogLine>();
        foreach (var record in records)
        {
            explainer.Explain(record, lines);

            // The first wrong value is said in the cycle its instruction left the pipeline,
            // after whatever else that cycle did.
            if (divergence is not null && divergence.Cycle == record.Cycle)
            {
                lines.Add(new LogLine(record.Cycle, LogKind.Wrong, explainer.Describe(divergence), Seq: 0));
            }
        }

        return lines;
    }
}
