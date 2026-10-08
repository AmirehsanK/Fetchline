using System.Globalization;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>The first instruction on which the pipeline and the reference machine disagreed.</summary>
/// <param name="Index">Which instruction it was, counting commits from one.</param>
/// <param name="Cycle">The cycle in which the pipeline committed it.</param>
public sealed record Divergence(ulong Index, ulong Cycle, Commit Pipeline, Commit Reference)
{
    /// <summary>What differed, as a sentence that names the instruction and the first wrong value.</summary>
    public string Describe()
    {
        var (ours, theirs) = (Pipeline, Reference);
        var where = string.Create(CultureInfo.InvariantCulture, $"instruction {Index}, in cycle {Cycle}");

        if (ours.Pc != theirs.Pc || ours.Instruction.Raw != theirs.Instruction.Raw)
        {
            return $"{where}: the pipeline committed {Text(ours)} where the reference machine ran {Text(theirs)}";
        }

        var what = Text(ours);
        if (ours.Register != theirs.Register || ours.Value != theirs.Value)
        {
            return $"{where}, {what}: the pipeline wrote {Written(ours)}, the reference machine wrote {Written(theirs)}";
        }

        if (ours.StoreBytes != theirs.StoreBytes || ours.StoreAddress != theirs.StoreAddress || ours.StoreValue != theirs.StoreValue)
        {
            return $"{where}, {what}: the pipeline stored {Stored(ours)}, the reference machine stored {Stored(theirs)}";
        }

        if (ours.NextPc != theirs.NextPc)
        {
            return $"{where}, {what}: the pipeline went on to {Hex(ours.NextPc)}, the reference machine to {Hex(theirs.NextPc)}";
        }

        return $"{where}, {what}: the pipeline's record is {ours}, the reference machine's is {theirs}";
    }

    private static string Text(in Commit commit) =>
        commit.Stop is StopReason.EndOfProgram
            ? "the end of the program"
            : $"'{Disassembler.Format(commit.Instruction.Raw, commit.Pc)}' at {Hex(commit.Pc)}";

    private static string Written(in Commit commit) =>
        commit.Register == 0 ? "no register" : $"{Registers.Name(commit.Register)} = {Hex(commit.Value)}";

    private static string Stored(in Commit commit) =>
        commit.StoreBytes == 0 ? "nothing" : $"{Hex(commit.StoreValue)} to {Hex(commit.StoreAddress)}";

    private static string Hex(uint value) => "0x" + value.ToString("x8", CultureInfo.InvariantCulture);
}

/// <summary>
/// Runs the pipeline with the reference machine beside it. Every time the pipeline commits an
/// instruction, the reference machine is stepped once and the two records are compared. They
/// came out of the same <see cref="Hart.Complete"/>, so a difference can only mean the pipeline's
/// plumbing handed it something else: a stale operand, a wrong-path instruction, a missed flush.
/// </summary>
public sealed class Lockstep
{
    public Lockstep(Program program, TextWriter? output = null, ExecutionEnvironment? environment = null)
    {
        Pipeline = new PipelineMachine(program, output, environment);

        // The reference machine runs the same system calls; what it prints is not wanted twice.
        Reference = new ReferenceMachine(program, TextWriter.Null, environment);
    }

    public PipelineMachine Pipeline { get; }

    public ReferenceMachine Reference { get; }

    /// <summary>How many records have been compared.</summary>
    public ulong Compared { get; private set; }

    /// <summary>The first disagreement, once there has been one. Nothing is compared after it.</summary>
    public Divergence? Divergence { get; private set; }

    /// <summary>Runs the pipeline for one cycle and checks whatever it committed.</summary>
    public CycleRecord Step()
    {
        var record = Pipeline.Step();
        if (record.Commit is { } commit)
        {
            Check(commit, record.Cycle);
        }

        if (record.End is { } end)
        {
            Check(end, record.Cycle);
        }

        return record;
    }

    /// <summary>
    /// Runs until the pipeline stops, the machines disagree, or <paramref name="maxCycles"/> have
    /// passed. Returns the record of the last cycle run.
    /// </summary>
    public CycleRecord? Run(ulong maxCycles = ulong.MaxValue)
    {
        CycleRecord? record = null;
        for (ulong i = 0; i < maxCycles && !Pipeline.IsFinished && Divergence is null; i++)
        {
            record = Step();
            if (record.Stop != StopReason.None)
            {
                break;
            }
        }

        return record;
    }

    private void Check(in Commit actual, ulong cycle)
    {
        if (Divergence is not null)
        {
            return;
        }

        Compared++;
        var expected = Reference.Step();

        // The cycle counter is the one thing the two machines are allowed to see differently: it
        // counts clock cycles, and the reference machine has no clock. The pipeline's reading is
        // given to the reference machine so that whatever the program does with it stays in step.
        if (expected.Register != 0 && actual.Register == expected.Register
            && actual.Instruction.Control.IsCsr && IsCycleCounter(actual.Instruction.Imm))
        {
            Reference.Hart.X[expected.Register] = actual.Value;
            expected = expected with { Value = actual.Value };
        }

        if (actual != expected)
        {
            Divergence = new Divergence(Compared, cycle, actual, expected);
        }
    }

    private static bool IsCycleCounter(int csr) => csr is Csr.Mcycle or Csr.Cycle or Csr.Mcycleh or Csr.Cycleh;
}
