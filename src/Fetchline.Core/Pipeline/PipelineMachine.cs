using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>
/// The five-stage pipeline: IF, ID, EX, MEM, WB, with the four latches between them.
///
/// A cycle is computed in two phases. First every stage works out its result from the latches as
/// they stand, in the order WB, MEM, EX, ID, IF; then all the latches take their new values at
/// once. The backwards order is what lets a signal produced late in the pipeline act on an
/// earlier stage in the same cycle, as a wire would: the register file is written before it is
/// read. Nothing a stage computes can leak into another stage's inputs except by being such a
/// signal, so there are no accidents of evaluation order.
/// </summary>
public sealed class PipelineMachine
{
    private IfId _ifId;
    private IdEx _idEx;
    private ExMem _exMem;
    private MemWb _memWb;

    private uint _pc;
    private ulong _nextSeq = 1;

    // Fetch has run into the end of the code, or into something that is not code. It is not an
    // instruction and occupies no stage; it takes effect once everything before it has completed.
    private Commit? _pendingEnd;

    public PipelineMachine(Program program, TextWriter? output = null, ExecutionEnvironment? environment = null)
    {
        Hart = new Hart(program, output, environment);
        _pc = Hart.Pc;
    }

    /// <summary>The registers, memory and surroundings. Its program counter is not the pipeline's.</summary>
    public Hart Hart { get; }

    /// <summary>How many cycles have been run.</summary>
    public ulong Cycles => Hart.Cycle;

    /// <summary>Why the machine stopped, or <see cref="StopReason.None"/> while it is running.</summary>
    public StopReason Stopped { get; private set; }

    public bool IsFinished => Stopped is not (StopReason.None or StopReason.Breakpoint);

    /// <summary>Runs one clock cycle and returns the record of it.</summary>
    public CycleRecord Step()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException("The machine has stopped.");
        }

        var hart = Hart;
        var x = hart.X;

        // ---- WB: the result is written to its register -------------------------------------
        Commit? committed = null;
        var writeBackView = default(StageView);
        if (_memWb.Valid)
        {
            var commit = _memWb.Commit;
            if (commit.Register != 0)
            {
                x[commit.Register] = commit.Value;
            }

            committed = commit;
            writeBackView = new StageView(Occupancy.Normal, _memWb.Seq, commit.Pc, commit.Instruction.Raw);
        }

        // ---- MEM: memory is read or written; this is the commit point ------------------------
        var nextMemWb = default(MemWb);
        var memoryView = default(StageView);
        if (_exMem.Valid)
        {
            var commit = hart.Complete(
                _exMem.Pc, _exMem.Instruction, _exMem.Rs1, _exMem.Rs2, _exMem.Alu, _exMem.Taken, _exMem.Target);
            nextMemWb = new MemWb(true, _exMem.Seq, commit);
            memoryView = new StageView(Occupancy.Normal, _exMem.Seq, _exMem.Pc, _exMem.Instruction.Raw);
        }

        // ---- EX: the ALU, and the branch decision --------------------------------------------
        var nextExMem = default(ExMem);
        var executeView = default(StageView);
        if (_idEx.Valid)
        {
            var instruction = _idEx.Instruction;
            var control = instruction.Control;

            // The values read in ID may be out of date: an instruction one or two ahead may have
            // computed a newer one that is not in the register file yet.
            var rs1 = Forward(instruction.Rs1, control.UsesRs1, _idEx.Rs1);
            var rs2 = Forward(instruction.Rs2, control.UsesRs2, _idEx.Rs2);

            var alu = Exec.Alu(
                control.Alu,
                Exec.OperandA(control.SrcA, rs1, _idEx.Pc),
                Exec.OperandB(control.SrcB, rs2, instruction.Imm));
            var taken = Exec.Taken(control.Branch, rs1, rs2);
            var target = Exec.Target(control.Branch, _idEx.Pc, instruction.Imm, alu);

            nextExMem = new ExMem(true, _idEx.Seq, _idEx.Pc, instruction, rs1, rs2, alu, taken, target);
            executeView = new StageView(Occupancy.Normal, _idEx.Seq, _idEx.Pc, instruction.Raw);
        }

        // ---- ID: decode, and read the source registers ----------------------------------------
        // WB has already written this cycle, so a value written now is the value read now.
        var nextIdEx = default(IdEx);
        var decodeView = default(StageView);
        if (_ifId.Valid)
        {
            var instruction = Decoder.Decode(_ifId.Raw);
            nextIdEx = new IdEx(true, _ifId.Seq, _ifId.Pc, instruction, x[instruction.Rs1], x[instruction.Rs2]);
            decodeView = new StageView(Occupancy.Normal, _ifId.Seq, _ifId.Pc, _ifId.Raw);
        }

        // ---- IF: read the next instruction ---------------------------------------------------
        var nextIfId = default(IfId);
        var fetchView = default(StageView);
        if (_pendingEnd is null)
        {
            if (hart.TryFetch(_pc, out var word, out var stop))
            {
                nextIfId = new IfId(true, _nextSeq++, _pc, word);
                fetchView = new StageView(Occupancy.Normal, nextIfId.Seq, _pc, word);
                _pc += 4;
            }
            else
            {
                _pendingEnd = stop;
            }
        }

        // ---- The clock edge: every latch takes its new value at once ----------------------------
        _memWb = nextMemWb;
        _exMem = nextExMem;
        _idEx = nextIdEx;
        _ifId = nextIfId;

        // A run that reaches the end of its code is over when the last instruction has left WB.
        Commit? end = null;
        if (_pendingEnd is { } pending && !(_ifId.Valid || _idEx.Valid || _exMem.Valid || _memWb.Valid))
        {
            end = pending;
            Stopped = pending.Stop;
        }

        hart.Cycle++;
        return new CycleRecord
        {
            Cycle = hart.Cycle,
            Fetch = fetchView,
            Decode = decodeView,
            Execute = executeView,
            Memory = memoryView,
            WriteBack = writeBackView,
            Commit = committed,
            End = end,
        };
    }

    /// <summary>
    /// Runs until the machine stops, or for at most <paramref name="maxCycles"/> cycles, and
    /// returns the record of the last cycle run.
    /// </summary>
    public CycleRecord? Run(ulong maxCycles = ulong.MaxValue)
    {
        CycleRecord? record = null;
        for (ulong i = 0; i < maxCycles && !IsFinished; i++)
        {
            record = Step();
            if (record.Stop != StopReason.None)
            {
                break;
            }
        }

        return record;
    }

    /// <summary>
    /// The forwarding unit, for one operand of the instruction in EX. The newest value of a
    /// register is, in order: the result of the instruction now in MEM, the result of the one now
    /// in WB, and only then what was read from the register file in ID.
    /// </summary>
    /// <param name="used">
    /// Whether the instruction really reads this register. A field that only happens to hold a
    /// register number (the constant of a CSR-immediate, say) must not be forwarded to.
    /// </param>
    private uint Forward(int register, bool used, uint fromDecode)
    {
        if (!used || register == 0)
        {
            return fromDecode;
        }

        if (_exMem.Valid && _exMem.Instruction.Control is { WritesRd: true } producer && _exMem.Instruction.Rd == register)
        {
            // In MEM an arithmetic result or a link address is already known. A value that only
            // exists after MEM (a load) is not, and nothing older may stand in for it.
            return producer.Wb switch
            {
                WbSrc.Alu => _exMem.Alu,
                WbSrc.PcPlus4 => _exMem.Pc + 4,
                _ => fromDecode,
            };
        }

        if (_memWb.Valid && _memWb.Commit.Register == register)
        {
            return _memWb.Commit.Value;
        }

        return fromDecode;
    }

    // The four latches. Each carries the sequence number given at fetch, so that one instruction
    // can be followed from stage to stage, and the address and word that identify it.

    private readonly record struct IfId(bool Valid, ulong Seq, uint Pc, uint Raw);

    private readonly record struct IdEx(bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2);

    private readonly record struct ExMem(
        bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2, uint Alu, bool Taken, uint Target);

    private readonly record struct MemWb(bool Valid, ulong Seq, Commit Commit);
}
