using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>Where the program counter's next value came from.</summary>
public enum NextPcFrom : byte
{
    /// <summary>Nowhere: nothing was fetched, or fetch was held, and the counter stays as it is.</summary>
    Held,

    /// <summary>The adder: the instruction after the one fetched.</summary>
    Sequential,

    /// <summary>The predictor: where it says the instruction fetched will lead.</summary>
    Predicted,

    /// <summary>A branch decided in ID that fetch had not followed.</summary>
    Decode,

    /// <summary>A branch decided in EX that fetch had not followed.</summary>
    Execute,

    /// <summary>The commit point: a system instruction, a trap or a stop.</summary>
    Memory,
}

/// <summary>
/// The values that were on the datapath's wires during one cycle, as the stages computed them.
/// A view of the datapath shows these and works none of them out: what the ALU was given after
/// forwarding is what the engine gave it, not what a drawing supposes it must have been.
///
/// A value belongs to the instruction in its stage that cycle, and means nothing when the stage
/// is empty. Where an instruction does not use a wire (the second operand of an instruction
/// with none, say) the value is whatever was there, as it would be in hardware; whether a wire
/// is in use is said by the instruction's control signals, not by its value.
/// </summary>
/// <param name="NextPc">What the program counter holds after the cycle.</param>
/// <param name="DecodeRs1">
/// What ID took for <c>rs1</c>: read from the register file, or, for a branch decided in ID,
/// what its comparator was given after forwarding.
/// </param>
/// <param name="ExecuteRs1">The value of <c>rs1</c> in EX after forwarding.</param>
/// <param name="AluA">The ALU's first operand: <c>rs1</c>, the program counter or zero.</param>
/// <param name="AluB">The ALU's second operand: <c>rs2</c> or the immediate.</param>
/// <param name="AluOut">
/// The ALU's result. Zero in the cycles a multiply or divide is still at work, when it has none.
/// </param>
/// <param name="MemoryAlu">The ALU result of the instruction in MEM: an address, or its result.</param>
/// <param name="MemoryRs2">The value of <c>rs2</c> of the instruction in MEM: what a store writes.</param>
public readonly record struct Wires(
    uint NextPc,
    NextPcFrom NextPcFrom,
    uint DecodeRs1,
    uint DecodeRs2,
    uint ExecuteRs1,
    uint ExecuteRs2,
    uint AluA,
    uint AluB,
    uint AluOut,
    uint MemoryAlu,
    uint MemoryRs2)
{
    /// <summary>Adds the wires to a hash of the run.</summary>
    public void AddTo(ref TraceHash hash)
    {
        hash.Add(NextPc);
        hash.Add((byte)NextPcFrom);
        hash.Add(DecodeRs1);
        hash.Add(DecodeRs2);
        hash.Add(ExecuteRs1);
        hash.Add(ExecuteRs2);
        hash.Add(AluA);
        hash.Add(AluB);
        hash.Add(AluOut);
        hash.Add(MemoryAlu);
        hash.Add(MemoryRs2);
    }
}
