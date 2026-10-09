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

    /// <summary>
    /// A branch decided in ID needs an operand that an instruction ahead of it is still
    /// computing: one in EX, or a load that has not finished MEM.
    /// </summary>
    BranchOperand,

    /// <summary>
    /// The instruction is a multiply or divide that takes several cycles in EX, and has not had
    /// them all. It waits for nothing but itself; what is behind it waits for it.
    /// </summary>
    MultiCycle,

    /// <summary>
    /// The instruction in IF is not in the instruction cache, and waits there for its block.
    /// Nothing else waits: bubbles go on to ID in its place.
    /// </summary>
    InstructionCacheMiss,

    /// <summary>
    /// The instruction in MEM reads or writes a block that is not in the data cache. It waits
    /// there for the block, and everything behind it waits for it.
    /// </summary>
    DataCacheMiss,
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
/// <param name="Seq">The consumer.</param>
/// <param name="Producer">The instruction whose result it is.</param>
/// <param name="To">
/// The stage the value was delivered to: EX for the ALU, or ID for the comparator of a branch
/// that is decided there.
/// </param>
public sealed record ForwardEvent(
    ulong Seq, ForwardSource From, Operand Operand, byte Register, uint Value, ulong Producer, Stage To = Stage.Execute)
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
        hash.Add((byte)To);
    }
}

/// <summary>
/// An instruction was kept in its stage for a cycle, and so was everything behind it. There is
/// one event for each cycle of waiting, about the instruction that is the reason for it.
/// </summary>
/// <param name="Seq">The instruction that waits.</param>
/// <param name="Stage">Where it waits.</param>
/// <param name="Register">The register it is waiting for; zero when it is waiting for none.</param>
/// <param name="Producer">The instruction that will produce it; zero when there is none.</param>
/// <param name="ProducerStage">The stage that instruction is in while this one waits.</param>
/// <param name="Remaining">
/// For an instruction that takes several cycles in its stage: how many more it needs after this
/// one. Zero for an instruction that is waiting for another.
/// </param>
public sealed record StallEvent(
    ulong Seq, StallCause Cause, Stage Stage, byte Register, ulong Producer, Stage ProducerStage = Stage.Execute,
    byte Remaining = 0)
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
        hash.Add(Remaining);
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

/// <summary>Which of the two caches.</summary>
public enum CacheKind : byte
{
    /// <summary>The one in front of IF.</summary>
    Instruction,

    /// <summary>The one in front of MEM.</summary>
    Data,
}

/// <summary>
/// A cache was asked for an address: by IF for an instruction, or by MEM for a load or a store.
/// There is one for every access, hit or miss, so that what a cache holds can be worked out
/// from the records like everything else.
/// </summary>
/// <param name="Seq">The instruction that asked.</param>
/// <param name="Way">The way the block was found in, or was brought into.</param>
/// <param name="Evicted">A block was put out to make room, and <paramref name="EvictedTag"/> is its tag.</param>
public sealed record CacheEvent(
    ulong Seq, CacheKind Kind, uint Address, bool Hit, int Set, int Way, uint Tag, bool Evicted, uint EvictedTag)
    : PipelineEvent(Seq)
{
    public override void AddTo(ref TraceHash hash)
    {
        hash.Add((byte)10);
        hash.Add(Seq);
        hash.Add((byte)Kind);
        hash.Add(Address);
        hash.Add(Hit);
        hash.Add((uint)Set);
        hash.Add((uint)Way);
        hash.Add(Tag);
        hash.Add(Evicted);
        hash.Add(EvictedTag);
    }
}
