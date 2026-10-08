using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>The latch a forwarded value was taken from.</summary>
public enum ForwardSource : byte
{
    /// <summary>EX/MEM: the result of the instruction one ahead, now in MEM.</summary>
    ExMem,

    /// <summary>MEM/WB: the result of the instruction two ahead, now in WB.</summary>
    MemWb,
}

/// <summary>Which operand of an instruction.</summary>
public enum Operand : byte
{
    /// <summary>The first: <c>rs1</c>.</summary>
    A,

    /// <summary>The second: <c>rs2</c>.</summary>
    B,
}

public enum StallCause : byte
{
    /// <summary>The instruction needs a value that the load ahead of it has not read yet.</summary>
    LoadUse,

    /// <summary>
    /// With no forwarding, the instruction needs a value that is not in the register file yet,
    /// and waits until the instruction producing it reaches WB.
    /// </summary>
    DataHazard,
}

public enum FlushCause : byte
{
    /// <summary>A branch or a jump went somewhere fetch had not followed.</summary>
    Branch,

    /// <summary>A system instruction took effect, and what is behind it must be fetched again.</summary>
    System,

    /// <summary>An instruction trapped.</summary>
    Trap,

    /// <summary>An instruction stopped the machine.</summary>
    Stop,
}

/// <summary>
/// Something the pipeline did in a cycle, said by the logic that did it. A hazard is never
/// worked out afterwards from what the stages held: the forwarding unit says it forwarded, the
/// hazard unit says it stalled and why. Diagrams, counters and sentences are all made from
/// these, so none of them can disagree with the engine.
/// </summary>
/// <param name="Seq">The instruction the event is about.</param>
public abstract record PipelineEvent(ulong Seq)
{
    /// <summary>Adds the event to a hash of the run, field by field.</summary>
    public abstract void AddTo(ref TraceHash hash);
}

/// <summary>An operand was taken from a latch because the register file did not have it yet.</summary>
/// <param name="Seq">The consumer, in EX.</param>
/// <param name="Producer">The instruction whose result it is.</param>
public sealed record ForwardEvent(ulong Seq, ForwardSource From, Operand Operand, byte Register, uint Value, ulong Producer)
    : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)1);
        hash.Add(Seq);
        hash.Add((byte)From);
        hash.Add((byte)Operand);
        hash.Add(Register);
        hash.Add(Value);
        hash.Add(Producer);
    }
}

/// <summary>An instruction was kept in its stage for a cycle.</summary>
/// <param name="Seq">The instruction that waits.</param>
/// <param name="Stage">Where it waits.</param>
/// <param name="Register">The register it is waiting for.</param>
/// <param name="Producer">The instruction that will produce it.</param>
/// <param name="ProducerStage">The stage that instruction is in while this one waits.</param>
public sealed record StallEvent(
    ulong Seq, StallCause Cause, Stage Stage, byte Register, ulong Producer, Stage ProducerStage = Stage.Execute)
    : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)2);
        hash.Add(Seq);
        hash.Add((byte)Cause);
        hash.Add((byte)Stage);
        hash.Add(Register);
        hash.Add(Producer);
        hash.Add((byte)ProducerStage);
    }
}

/// <summary>An instruction was thrown away. There is one event for each instruction squashed.</summary>
/// <param name="Seq">The instruction that was squashed.</param>
/// <param name="Stage">The stage it was in.</param>
/// <param name="By">The instruction that caused it.</param>
public sealed record FlushEvent(ulong Seq, FlushCause Cause, Stage Stage, ulong By) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)3);
        hash.Add(Seq);
        hash.Add((byte)Cause);
        hash.Add((byte)Stage);
        hash.Add(By);
    }
}

/// <summary>A branch or a jump was decided.</summary>
/// <param name="Target">Where it goes when taken.</param>
/// <param name="Mispredicted">Fetch had gone the other way, so what it fetched was squashed.</param>
/// <param name="ResolvedIn">The stage that decided it.</param>
public sealed record BranchEvent(ulong Seq, bool Taken, uint Target, bool Mispredicted, Stage ResolvedIn)
    : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)4);
        hash.Add(Seq);
        hash.Add(Taken);
        hash.Add(Target);
        hash.Add(Mispredicted);
        hash.Add((byte)ResolvedIn);
    }
}

/// <summary>A register was written, in WB.</summary>
public sealed record RegWriteEvent(ulong Seq, byte Register, uint Value) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)5);
        hash.Add(Seq);
        hash.Add(Register);
        hash.Add(Value);
    }
}

/// <summary>Memory was read, in MEM.</summary>
/// <param name="Value">What was read, after sign or zero extension.</param>
public sealed record MemReadEvent(ulong Seq, uint Address, byte Bytes, uint Value) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)6);
        hash.Add(Seq);
        hash.Add(Address);
        hash.Add(Bytes);
        hash.Add(Value);
    }
}

/// <summary>Memory was written, in MEM.</summary>
public sealed record MemWriteEvent(ulong Seq, uint Address, byte Bytes, uint Value) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)7);
        hash.Add(Seq);
        hash.Add(Address);
        hash.Add(Bytes);
        hash.Add(Value);
    }
}

/// <summary>An instruction left WB. Its record is the cycle's <see cref="CycleRecord.Commit"/>.</summary>
public sealed record CommitEvent(ulong Seq) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)8);
        hash.Add(Seq);
    }
}

/// <summary>An instruction trapped, in MEM.</summary>
/// <param name="Handler">Where control went: the trap vector.</param>
public sealed record TrapEvent(ulong Seq, uint Cause, uint Value, uint Handler) : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)9);
        hash.Add(Seq);
        hash.Add(Cause);
        hash.Add(Value);
        hash.Add(Handler);
    }
}
