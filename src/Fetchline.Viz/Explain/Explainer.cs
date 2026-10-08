using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Explain;

public enum LogKind : byte
{
    Stall,
    Forward,
    Flush,
    Trap,
}

/// <summary>One line of the hazard log: something the pipeline did, said as a sentence.</summary>
/// <param name="Seq">The instruction the line is about: the one that waited, received or caused.</param>
/// <param name="Other">The other instruction involved (the producer), or zero.</param>
public sealed record LogLine(ulong Cycle, LogKind Kind, string Text, ulong Seq, ulong Other = 0);

/// <summary>
/// Turns the events of a run into the hazard log. An event already says what happened and why;
/// this only finds the words, and the names of the instructions involved.
/// </summary>
public sealed class Explainer(StaircaseLayout layout, InstructionLabels labels, IMessages messages)
{
    public IMessages Messages { get; } = messages;

    /// <summary>The log lines of a whole run, in order.</summary>
    public List<LogLine> Explain(IEnumerable<CycleRecord> records)
    {
        var lines = new List<LogLine>();
        foreach (var record in records)
        {
            Explain(record, lines);
        }

        return lines;
    }

    /// <summary>Adds the log lines of one cycle.</summary>
    public void Explain(CycleRecord record, List<LogLine> lines)
    {
        foreach (var item in record.Events)
        {
            switch (item)
            {
                case StallEvent { Cause: StallCause.LoadUse } stall:
                    lines.Add(new LogLine(
                        record.Cycle, LogKind.Stall,
                        Messages.LoadUse(Name(stall.Seq), labels.Register(stall.Register), Name(stall.Producer)),
                        stall.Seq, stall.Producer));
                    break;

                case StallEvent { Cause: StallCause.DataHazard } stall:
                    lines.Add(new LogLine(
                        record.Cycle, LogKind.Stall,
                        Messages.DataHazard(
                            Name(stall.Seq), labels.Register(stall.Register), Name(stall.Producer),
                            Export.AsciiTrace.Name(stall.ProducerStage)),
                        stall.Seq, stall.Producer));
                    break;

                case ForwardEvent forward:
                    lines.Add(new LogLine(
                        record.Cycle, LogKind.Forward,
                        Messages.Forwarded(
                            forward.From == ForwardSource.ExMem ? "EX/MEM" : "MEM/WB",
                            forward.Operand.ToString(),
                            labels.Register(forward.Register),
                            Name(forward.Producer)),
                        forward.Seq, forward.Producer));
                    break;
            }
        }

        // The instructions one redirect squashed are one thing that happened, so one line.
        var flushes = record.Events.OfType<FlushEvent>().ToList();
        var trap = record.Events.OfType<TrapEvent>().FirstOrDefault();
        if (trap is not null)
        {
            lines.Add(new LogLine(
                record.Cycle, LogKind.Trap,
                Messages.Trapped(Name(trap.Seq), TrapCause.Name(trap.Cause), labels.Address(trap.Handler), flushes.Count),
                trap.Seq));
        }
        else if (flushes.Count > 0)
        {
            var by = flushes[0].By;
            var text = flushes[0].Cause switch
            {
                FlushCause.Branch => Messages.TakenBranch(
                    Name(by),
                    labels.Address(record.Events.OfType<BranchEvent>().FirstOrDefault(branch => branch.Seq == by)?.Target ?? 0),
                    flushes.Count),
                FlushCause.Stop => Messages.Stopped(Name(by), flushes.Count),
                _ => Messages.SystemFlush(Name(by), flushes.Count),
            };
            lines.Add(new LogLine(record.Cycle, LogKind.Flush, text, by));
        }
    }

    /// <summary>The word a line uses for a kind of event.</summary>
    public string Label(LogKind kind) => kind switch
    {
        LogKind.Stall => Messages.Stall,
        LogKind.Forward => Messages.Forward,
        LogKind.Flush => Messages.Flush,
        _ => Messages.Trap,
    };

    /// <summary>An instruction is named in a sentence by its mnemonic, as its row shows it.</summary>
    private string Name(ulong seq) =>
        layout.Row(seq) is { } row ? labels.Describe(row.Pc, row.Raw).Mnemonic : "?";
}
