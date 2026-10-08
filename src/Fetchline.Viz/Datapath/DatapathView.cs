using System.Globalization;
using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Explain;

namespace Fetchline.Viz.Datapath;

/// <summary>One wire during one cycle.</summary>
/// <param name="Active">Something used the wire this cycle: it is one of the bright ones.</param>
/// <param name="Value">
/// What was on it, as text: a word in hex, or the name of a register. Null when the wire was
/// not in use, and for a wire that carries a decision and no value.
/// </param>
public readonly record struct WireReading(Wire Wire, bool Active, string? Value)
{
    /// <summary>What to say about the wire to someone pointing at it.</summary>
    public string Describe(IMessages messages)
    {
        var signal = messages.NameOf(Wire.Signal);
        return !Active ? messages.WireIdle(signal)
            : Value is null ? messages.WireAsserted(signal)
            : messages.WireCarries(signal, Value);
    }
}

/// <summary>
/// The datapath during one cycle: which wires were in use and what was on each, and which parts
/// were at work. All of it is read from the cycle's record: the stages, the events, and the
/// values the engine noted on its wires. Which wires an instruction uses is said by its control
/// signals, which are the same ones the engine is driven by.
/// </summary>
public sealed class DatapathView
{
    private readonly Dictionary<string, WireReading> _byId;
    private readonly HashSet<string> _working;

    private DatapathView(DatapathModel model, IReadOnlyList<WireReading> wires, HashSet<string> working)
    {
        Model = model;
        Wires = wires;
        _working = working;
        _byId = wires.ToDictionary(reading => reading.Wire.Id);
    }

    public DatapathModel Model { get; }

    /// <summary>Every wire of the model, in the model's order.</summary>
    public IReadOnlyList<WireReading> Wires { get; }

    /// <summary>The reading of one wire. The wire has to be one this datapath has.</summary>
    public WireReading this[string wire] => _byId[wire];

    /// <summary>The part was at work this cycle: a wire in use begins or ends at it, or it decided something.</summary>
    public bool IsWorking(string part) => _working.Contains(part);

    /// <summary>The datapath during a cycle; with no record, at reset, when nothing is in use.</summary>
    /// <param name="registers">How registers are named on the wires that carry a register's number.</param>
    public static DatapathView Of(DatapathModel model, CycleRecord? record, RegisterStyle registers = RegisterStyle.Abi)
    {
        var active = new Dictionary<string, string?>();
        var working = new HashSet<string>();
        if (record is not null)
        {
            Read(record, registers, model.Has("fwd-a"), active, working);
        }

        var readings = new List<WireReading>(model.Wires.Count);
        foreach (var wire in model.Wires)
        {
            var isActive = active.TryGetValue(wire.Id, out var value);
            readings.Add(new WireReading(wire, isActive, value));
            if (isActive)
            {
                working.Add(wire.From);
                working.Add(wire.To);
            }
        }

        working.RemoveWhere(part => !model.Has(part));
        return new DatapathView(model, readings, working);
    }

    /// <summary>
    /// Says of every wire a pipeline could have whether it was in use. Wires the datapath in
    /// hand does not have are dropped by the caller; all that is asked about how it is built is
    /// whether the operands in EX go through forwarding multiplexers on their way.
    /// </summary>
    private static void Read(
        CycleRecord record, RegisterStyle registers, bool forwarding, Dictionary<string, string?> active, HashSet<string> working)
    {
        var wires = record.Wires;
        var events = record.Events;

        void Lit(string wire, bool when, string? value = null)
        {
            if (when)
            {
                active[wire] = value;
            }
        }

        ForwardEvent? Forwarded(Stage to, Operand operand)
        {
            foreach (var item in events)
            {
                if (item is ForwardEvent forward && forward.To == to && forward.Operand == operand)
                {
                    return forward;
                }
            }

            return null;
        }

        // ---- IF: the program counter, and where its next value comes from ----------------------
        var fetch = record.Fetch;
        var next = Hex(wires.NextPc);
        Lit("pc-imem", fetch.HasInstruction, Hex(fetch.Pc));
        Lit("pc-add", fetch.HasInstruction, Hex(fetch.Pc));
        Lit("pc-pred", fetch.HasInstruction, Hex(fetch.Pc));
        Lit("pc-ifid", fetch.State == Occupancy.Normal, Hex(fetch.Pc));
        Lit("imem-ifid", fetch.State == Occupancy.Normal, Hex(fetch.Raw));
        Lit("add-mux", wires.NextPcFrom == NextPcFrom.Sequential, next);
        Lit("pred-mux", wires.NextPcFrom == NextPcFrom.Predicted, next);
        Lit("redirect-id", wires.NextPcFrom == NextPcFrom.Decode, next);
        Lit("redirect-ex", wires.NextPcFrom == NextPcFrom.Execute, next);
        Lit("redirect-mem", wires.NextPcFrom == NextPcFrom.Memory, next);
        Lit("mux-pc", wires.NextPcFrom != NextPcFrom.Held, next);

        // Whatever is made to wait, it is the hazard unit that holds what is behind it.
        var stalled = events.Any(item => item is StallEvent);
        Lit("hazard-pc", stalled);
        Lit("hazard-ifid", stalled);

        // ---- ID: the register file and the immediate -------------------------------------------
        var decode = record.Decode;
        if (decode.HasInstruction)
        {
            var instruction = Decoder.Decode(decode.Raw);
            var control = instruction.Control;
            var moving = decode.State == Occupancy.Normal;
            var hasImmediate = control.SrcB == SrcB.Imm;

            Lit("id-rs1", control.UsesRs1, Registers.Name(instruction.Rs1, registers));
            Lit("id-rs2", control.UsesRs2, Registers.Name(instruction.Rs2, registers));
            Lit("id-inst", hasImmediate, Hex(decode.Raw));
            Lit("id-pc", moving, Hex(decode.Pc));
            Lit("id-a", moving && control.UsesRs1, Hex(wires.DecodeRs1));
            Lit("id-b", moving && control.UsesRs2, Hex(wires.DecodeRs2));
            Lit("id-imm", moving && hasImmediate, Hex((uint)instruction.Imm));

            // A branch decided here says so; one that is still waiting for an operand has
            // compared nothing yet.
            if (events.Any(item => item is BranchEvent { ResolvedIn: Stage.Decode } branch && branch.Seq == decode.Seq))
            {
                working.Add("cmp");
                var (a, b) = (Forwarded(Stage.Decode, Operand.A), Forwarded(Stage.Decode, Operand.B));
                Lit("cmp-a", control.UsesRs1 && a is null, Hex(wires.DecodeRs1));
                Lit("cmp-b", control.UsesRs2 && b is null, Hex(wires.DecodeRs2));
                Lit("cmp-forward", (a ?? b) is not null, Hex((a ?? b)?.Value ?? 0));
            }
        }

        // ---- EX: the operands, the ALU, and the branch -----------------------------------------
        var execute = record.Execute;
        if (execute.HasInstruction)
        {
            var instruction = Decoder.Decode(execute.Raw);
            var control = instruction.Control;
            var moving = execute.State == Occupancy.Normal;
            var (a, b) = (Forwarded(Stage.Execute, Operand.A), Forwarded(Stage.Execute, Operand.B));
            var (rs1, rs2) = (Hex(wires.ExecuteRs1), Hex(wires.ExecuteRs2));

            // What ID/EX holds is used unless something newer was forwarded in its place.
            Lit("ex-rs1", control.UsesRs1 && a is null && (forwarding || control.SrcA == SrcA.Rs1), rs1);
            Lit("ex-rs2", control.UsesRs2 && b is null && (forwarding || control.SrcB == SrcB.Rs2), rs2);
            Lit("fwd-exmem-a", a is { From: ForwardSource.ExMem }, Hex(a?.Value ?? 0));
            Lit("fwd-memwb-a", a is { From: ForwardSource.MemWb }, Hex(a?.Value ?? 0));
            Lit("fwd-exmem-b", b is { From: ForwardSource.ExMem }, Hex(b?.Value ?? 0));
            Lit("fwd-memwb-b", b is { From: ForwardSource.MemWb }, Hex(b?.Value ?? 0));
            Lit("forward-a", a is not null);
            Lit("forward-b", b is not null);

            Lit("fwd-a-out", control.UsesRs1 && control.SrcA == SrcA.Rs1, rs1);
            Lit("fwd-b-out", control.UsesRs2 && control.SrcB == SrcB.Rs2, rs2);
            Lit("ex-pc", control.SrcA == SrcA.Pc, Hex(execute.Pc));
            Lit("ex-imm", control.SrcB == SrcB.Imm, Hex((uint)instruction.Imm));
            Lit("alu-a", true, Hex(wires.AluA));
            Lit("alu-b", true, Hex(wires.AluB));
            Lit("alu-out", moving, Hex(wires.AluOut));
            Lit("store-data", moving && control.Mem == MemOp.Store, rs2);

            Lit("branch-a", control.IsConditionalBranch, rs1);
            Lit("branch-b", control.IsConditionalBranch, rs2);
            if (events.Any(item => item is BranchEvent { ResolvedIn: Stage.Execute }))
            {
                working.Add("branch");
            }
        }

        // ---- MEM: data memory, and the commit point --------------------------------------------
        var memory = record.Memory;
        if (memory.HasInstruction)
        {
            var control = Decoder.Decode(memory.Raw).Control;
            Lit("mem-address", control.Mem != MemOp.None, Hex(wires.MemoryAlu));
            Lit("mem-data", control.Mem == MemOp.Store, Hex(wires.MemoryRs2));
            if (events.OfType<MemReadEvent>().FirstOrDefault() is { } read)
            {
                Lit("mem-read", true, Hex(read.Value));
            }

            // A result that is not read from memory goes round it. What a CSR instruction read
            // is only known once it has taken effect, so its wire is in use and says no value.
            Lit("mem-result", control.WritesRd && control.Wb != WbSrc.Mem, control.Wb switch
            {
                WbSrc.Alu => Hex(wires.MemoryAlu),
                WbSrc.PcPlus4 => Hex(memory.Pc + 4),
                _ => null,
            });

            if (wires.NextPcFrom == NextPcFrom.Memory || events.Any(item => item is TrapEvent))
            {
                working.Add("commit");
            }
        }

        // ---- WB: the value goes back to the register file --------------------------------------
        if (record.WriteBack.HasInstruction && record.Commit is { } commit)
        {
            var control = commit.Instruction.Control;
            var writes = control.WritesRd && !commit.Trapped;
            Lit("wb-mem", writes && control.Wb == WbSrc.Mem, Hex(commit.Value));
            Lit("wb-result", writes && control.Wb != WbSrc.Mem, Hex(commit.Value));
            Lit("wb-write", commit.WritesRegister, Hex(commit.Value));
        }
    }

    private static string Hex(uint value) => "0x" + value.ToString("x8", CultureInfo.InvariantCulture);
}
