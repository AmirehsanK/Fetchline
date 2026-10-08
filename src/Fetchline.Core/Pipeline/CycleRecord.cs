using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>The five stages, in the order an instruction passes through them.</summary>
public enum Stage : byte
{
    /// <summary>IF: read the instruction word at the program counter.</summary>
    Fetch,

    /// <summary>ID: decode it and read its source registers.</summary>
    Decode,

    /// <summary>EX: compute in the ALU; decide a branch.</summary>
    Execute,

    /// <summary>MEM: read or write memory. This is also the commit point.</summary>
    Memory,

    /// <summary>WB: write the result to its register.</summary>
    WriteBack,
}

/// <summary>What is in a stage during a cycle.</summary>
public enum Occupancy : byte
{
    /// <summary>Nothing: a bubble, or the stage has not been reached yet.</summary>
    Empty,

    /// <summary>An instruction that does its work here and moves on at the end of the cycle.</summary>
    Normal,

    /// <summary>An instruction that is kept here for another cycle.</summary>
    Held,

    /// <summary>An instruction that is thrown away at the end of the cycle.</summary>
    Squashed,
}

/// <summary>One stage during one cycle: what is in it and what becomes of it.</summary>
/// <param name="Seq">
/// The number given to the instruction when it was fetched. One dynamic instruction keeps it
/// through all its stages, so it can be followed across cycles; an instruction fetched again
/// after a flush gets a new one.
/// </param>
/// <param name="Raw">The instruction word.</param>
public readonly record struct StageView(Occupancy State, ulong Seq, uint Pc, uint Raw)
{
    public bool HasInstruction => State != Occupancy.Empty;
}

/// <summary>
/// Everything the pipeline did in one clock cycle. Nothing outside the engine looks inside the
/// pipeline; diagrams, counters and explanations are all made from these records.
/// </summary>
public sealed class CycleRecord
{
    /// <summary>The number of the cycle, from one.</summary>
    public ulong Cycle { get; init; }

    public StageView Fetch { get; init; }

    public StageView Decode { get; init; }

    public StageView Execute { get; init; }

    public StageView Memory { get; init; }

    public StageView WriteBack { get; init; }

    /// <summary>The record of the instruction that left WB this cycle, if one did.</summary>
    public Commit? Commit { get; init; }

    /// <summary>
    /// The record that ends a run without an instruction: fetch reached the end of the code, or
    /// somewhere that is not code, and everything fetched before it has now completed.
    /// </summary>
    public Commit? End { get; init; }

    /// <summary>
    /// What happened in the cycle, in the order the stages are worked out: WB, MEM, EX, ID, IF.
    /// Empty when the machine was told not to record.
    /// </summary>
    public IReadOnlyList<PipelineEvent> Events { get; init; } = [];

    /// <summary>The values on the datapath's wires during the cycle.</summary>
    public Wires Wires { get; init; }

    /// <summary>Why the machine stopped in this cycle, or <see cref="StopReason.None"/>.</summary>
    public StopReason Stop => End?.Stop ?? Commit?.Stop ?? StopReason.None;

    /// <summary>Adds the whole cycle to a hash of the run.</summary>
    public void AddTo(ref TraceHash hash)
    {
        hash.Add(Cycle);
        foreach (var stage in (ReadOnlySpan<StageView>)[Fetch, Decode, Execute, Memory, WriteBack])
        {
            hash.Add((byte)stage.State);
            hash.Add(stage.Seq);
            hash.Add(stage.Pc);
            hash.Add(stage.Raw);
        }

        hash.Add(Commit is not null);
        if (Commit is { } commit)
        {
            hash.Add(commit);
        }

        hash.Add(End is not null);
        if (End is { } end)
        {
            hash.Add(end);
        }

        hash.Add((uint)Events.Count);
        foreach (var item in Events)
        {
            item.AddTo(ref hash);
        }

        Wires.AddTo(ref hash);
    }

    /// <summary>The view of one stage.</summary>
    public StageView this[Stage stage] => stage switch
    {
        Stage.Fetch => Fetch,
        Stage.Decode => Decode,
        Stage.Execute => Execute,
        Stage.Memory => Memory,
        _ => WriteBack,
    };
}
