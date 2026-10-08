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

                case StallEvent { Cause: StallCause.BranchOperand } stall:
                    lines.Add(new LogLine(
                        record.Cycle, LogKind.Stall,
                        Messages.BranchOperand(
                            Name(stall.Seq), labels.Register(stall.Register), Name(stall.Producer),
                            Export.AsciiTrace.Name(stall.ProducerStage),
                            Export.AsciiTrace.Name(ReadyAfter(stall))),
                        stall.Seq, stall.Producer));
                    break;

                case ForwardEvent forward:
                    lines.Add(new LogLine(
                        record.Cycle, LogKind.Forward,
                        Messages.Forwarded(
                            forward.From == ForwardSource.ExMem ? "EX/MEM" : "MEM/WB",
                            Export.AsciiTrace.Name(forward.To),
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
            var decided = record.Events.OfType<BranchEvent>().FirstOrDefault(branch => branch.Seq == by);
            var text = flushes[0].Cause switch
            {
                FlushCause.Branch => Messages.TakenBranch(
                    Name(by),
                    Export.AsciiTrace.Name(decided?.ResolvedIn ?? Stage.Execute),
                    labels.Address(decided?.Target ?? 0),
                    flushes.Count),
                FlushCause.Stop => Messages.Stopped(Name(by), flushes.Count),
                _ => Messages.SystemFlush(Name(by), flushes.Count),
            };
            lines.Add(new LogLine(record.Cycle, LogKind.Flush, text, by));
        }
    }

    /// <summary>
    /// The first wrong value, as a sentence: which instruction it was, and what the pipeline did
    /// against what the reference machine did. With hazard handling off this is the lesson.
    /// </summary>
    public string Describe(Divergence divergence)
    {
        var (ours, theirs) = (divergence.Pipeline, divergence.Reference);
        string Hex(uint value) => "0x" + value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        string Text(in Core.Trace.Commit commit) => commit.Stop == Core.Trace.StopReason.EndOfProgram
            ? Messages.EndOfProgram
            : "'" + labels.Describe(commit.Pc, commit.Instruction.Raw) + "'";
        string Written(in Core.Trace.Commit commit) => commit.Register == 0
            ? Messages.NoRegister
            : labels.Register(commit.Register) + " = " + Hex(commit.Value);
        string Stored(in Core.Trace.Commit commit) => commit.StoreBytes == 0
            ? Messages.NoStore
            : Hex(commit.StoreValue) + " -> " + Hex(commit.StoreAddress);

        string detail;
        if (ours.Pc != theirs.Pc || ours.Instruction.Raw != theirs.Instruction.Raw)
        {
            detail = Messages.WrongInstruction(Text(ours), Text(theirs));
        }
        else if (ours.Register != theirs.Register || ours.Value != theirs.Value)
        {
            detail = Messages.WrongRegister(Written(ours), Written(theirs));
        }
        else if (ours.StoreBytes != theirs.StoreBytes || ours.StoreAddress != theirs.StoreAddress || ours.StoreValue != theirs.StoreValue)
        {
            detail = Messages.WrongStore(Stored(ours), Stored(theirs));
        }
        else
        {
            detail = Messages.WrongPath(labels.Address(ours.NextPc), labels.Address(theirs.NextPc));
        }

        return Messages.FirstWrongValue(divergence.Index, divergence.Cycle, Text(theirs), detail);
    }

    /// <summary>The word a line uses for a kind of event.</summary>
    public string Label(LogKind kind) => kind switch
    {
        LogKind.Stall => Messages.Stall,
        LogKind.Forward => Messages.Forward,
        LogKind.Flush => Messages.Flush,
        _ => Messages.Trap,
    };

    /// <summary>
    /// The stage after which the value a branch is waiting for exists: EX for something the ALU
    /// computes, MEM for something that has to be read from memory first.
    /// </summary>
    private Stage ReadyAfter(StallEvent stall)
    {
        var producer = layout.Row(stall.Producer);
        var isLoad = producer is not null && Decoder.Decode(producer.Raw).Control.Wb is WbSrc.Mem or WbSrc.Csr;
        return isLoad ? Stage.Memory : Stage.Execute;
    }

    /// <summary>An instruction is named in a sentence by its mnemonic, as its row shows it.</summary>
    private string Name(ulong seq) =>
        layout.Row(seq) is { } row ? labels.Describe(row.Pc, row.Raw).Mnemonic : "?";
}
