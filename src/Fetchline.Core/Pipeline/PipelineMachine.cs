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
/// read, a redirect from EX reaches IF, a flush from MEM reaches everything behind it. Nothing a
/// stage computes can leak into another stage's inputs except by being such a signal, so there
/// are no accidents of evaluation order.
/// </summary>
public sealed class PipelineMachine
{
    private IfId _ifId;
    private IdEx _idEx;
    private ExMem _exMem;
    private MemWb _memWb;

    private uint _pc;
    private ulong _nextSeq = 1;

    // The instruction that is waiting in IF because the stage ahead of it is stalled. It was
    // fetched once and is not fetched again: it keeps its sequence number while it waits.
    private IfId _heldFetch;

    // Fetch has run into the end of the code, or into something that is not code. It is not an
    // instruction and occupies no stage; it takes effect once everything before it has completed.
    private Commit? _pendingEnd;

    // An instruction that stops the machine has committed and is on its way to WB. Nothing more
    // is fetched; the run ends in the cycle it leaves.
    private bool _halting;

    // The events of the cycle being computed; null when not recording.
    private List<PipelineEvent>? _events;

    public PipelineMachine(
        Program program, TextWriter? output = null, ExecutionEnvironment? environment = null, PipelineConfig? config = null)
    {
        Hart = new Hart(program, output, environment);
        Config = config ?? PipelineConfig.Default;
        _pc = Hart.Pc;
    }

    /// <summary>How this pipeline is built.</summary>
    public PipelineConfig Config { get; }

    /// <summary>The registers, memory and surroundings. Its program counter is not the pipeline's.</summary>
    public Hart Hart { get; }

    /// <summary>How many cycles have been run.</summary>
    public ulong Cycles => Hart.Cycle;

    /// <summary>
    /// Whether each cycle's record lists its events. Turning it off makes a long run cheaper when
    /// only the commit records are wanted; what the machine computes is the same either way.
    /// </summary>
    public bool Recording { get; set; } = true;

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
        var fetchingAllowed = !_halting;
        _events = Recording ? [] : null;
        Stopped = StopReason.None;      // a pause at an ebreak ends when the machine is stepped again

        // ---- WB: the result is written to its register -------------------------------------
        Commit? committed = null;
        var writeBackView = default(StageView);
        if (_memWb.Valid)
        {
            var commit = _memWb.Commit;
            if (commit.Register != 0)
            {
                x[commit.Register] = commit.Value;
                _events?.Add(new RegWriteEvent(_memWb.Seq, commit.Register, commit.Value));
            }

            committed = commit;
            Stopped = commit.Stop;
            writeBackView = new StageView(Occupancy.Normal, _memWb.Seq, commit.Pc, commit.Instruction.Raw);
            _events?.Add(new CommitEvent(_memWb.Seq));
        }

        // ---- MEM: memory is read or written; this is the commit point ------------------------
        var nextMemWb = default(MemWb);
        var memoryView = default(StageView);
        var redirect = false;
        uint redirectTo = 0;
        var flush = false;
        var flushCause = FlushCause.System;
        ulong redirectBy = 0;
        if (_exMem.Valid)
        {
            var control = _exMem.Instruction.Control;
            var commit = hart.Complete(
                _exMem.Pc, _exMem.Instruction, _exMem.Rs1, _exMem.Rs2, _exMem.Alu, _exMem.Taken, _exMem.Target);
            nextMemWb = new MemWb(true, _exMem.Seq, commit);
            memoryView = new StageView(Occupancy.Normal, _exMem.Seq, _exMem.Pc, _exMem.Instruction.Raw);

            if (_events is not null)
            {
                if (control.Mem == MemOp.Load && commit.Stop == StopReason.None)
                {
                    var loaded = Exec.Extend(hart.Memory.Read(_exMem.Alu, control.MemBytes), control.MemBytes, control.MemSigned);
                    _events.Add(new MemReadEvent(_exMem.Seq, _exMem.Alu, control.MemBytes, loaded));
                }

                if (commit.WritesMemory)
                {
                    _events.Add(new MemWriteEvent(_exMem.Seq, commit.StoreAddress, commit.StoreBytes, commit.StoreValue));
                }

                if (commit.Trapped)
                {
                    _events.Add(new TrapEvent(_exMem.Seq, commit.Cause, commit.TrapValue, commit.NextPc));
                }
            }

            // A system instruction, a trap or a stop changes what the instructions behind it
            // should have seen, or whether they should run at all. All three of them are thrown
            // away and fetched again from where this instruction says control goes. Because this
            // happens here and nowhere earlier, a system call never runs on a wrong path, and an
            // instruction that traps has changed nothing.
            if (control.System != SystemOp.None || commit.Trapped || commit.Stop != StopReason.None)
            {
                flush = true;
                redirect = true;
                redirectTo = commit.NextPc;
                redirectBy = _exMem.Seq;
                _halting = commit.Stop is not (StopReason.None or StopReason.Breakpoint);
                flushCause = commit.Stop != StopReason.None ? FlushCause.Stop
                    : commit.Trapped ? FlushCause.Trap
                    : FlushCause.System;
            }
        }

        // ---- EX: the ALU, and the branch decision --------------------------------------------
        var nextExMem = default(ExMem);
        var executeView = default(StageView);
        if (_idEx.Valid && flush)
        {
            executeView = new StageView(Occupancy.Squashed, _idEx.Seq, _idEx.Pc, _idEx.Instruction.Raw);
            _events?.Add(new FlushEvent(_idEx.Seq, flushCause, Stage.Execute, redirectBy));
        }
        else if (_idEx.Valid)
        {
            var instruction = _idEx.Instruction;
            var control = instruction.Control;

            // The values read in ID may be out of date: an instruction one or two ahead may have
            // computed a newer one that is not in the register file yet.
            var rs1 = Forward(Operand.A, instruction.Rs1, control.UsesRs1, _idEx.Rs1);
            var rs2 = Forward(Operand.B, instruction.Rs2, control.UsesRs2, _idEx.Rs2);

            var alu = Exec.Alu(
                control.Alu,
                Exec.OperandA(control.SrcA, rs1, _idEx.Pc),
                Exec.OperandB(control.SrcB, rs2, instruction.Imm));
            var taken = Exec.Taken(control.Branch, rs1, rs2);
            var target = Exec.Target(control.Branch, _idEx.Pc, instruction.Imm, alu);

            // Fetch has carried on in a straight line behind this instruction. If it goes
            // somewhere else, the two instructions fetched since are from the wrong path.
            if (taken)
            {
                redirect = true;
                redirectTo = target;
                redirectBy = _idEx.Seq;
                flushCause = FlushCause.Branch;
            }

            if (control.IsControlFlow)
            {
                _events?.Add(new BranchEvent(_idEx.Seq, taken, target, Mispredicted: taken, Stage.Execute));
            }

            nextExMem = new ExMem(true, _idEx.Seq, _idEx.Pc, instruction, rs1, rs2, alu, taken, target);
            executeView = new StageView(Occupancy.Normal, _idEx.Seq, _idEx.Pc, instruction.Raw);
        }

        // ---- ID: decode, and read the source registers ----------------------------------------
        // WB has already written this cycle, so a value written now is the value read now.
        var nextIdEx = default(IdEx);
        var decodeView = default(StageView);
        var stall = false;
        if (_ifId.Valid)
        {
            var instruction = Decoder.Decode(_ifId.Raw);
            var control = instruction.Control;

            // A dependency counts only if the instruction really reads the register.
            var rs1 = control.UsesRs1 ? instruction.Rs1 : 0;
            var rs2 = control.UsesRs2 ? instruction.Rs2 : 0;
            if (!redirect && WaitsFor(rs1, rs2) is { } wait)
            {
                stall = true;
                _events?.Add(new StallEvent(_ifId.Seq, wait.Cause, Stage.Decode, wait.Register, wait.Producer, wait.ProducerStage));
            }

            if (!stall)
            {
                nextIdEx = new IdEx(true, _ifId.Seq, _ifId.Pc, instruction, x[instruction.Rs1], x[instruction.Rs2]);
            }

            decodeView = new StageView(stall ? Occupancy.Held : Occupancy.Normal, _ifId.Seq, _ifId.Pc, _ifId.Raw);
        }

        // ---- IF: read the next instruction ---------------------------------------------------
        var fetching = _heldFetch;
        if (!fetching.Valid && _pendingEnd is null && fetchingAllowed)
        {
            if (hart.TryFetch(_pc, out var word, out var stop))
            {
                fetching = new IfId(true, _nextSeq++, _pc, word);
            }
            else
            {
                _pendingEnd = stop;
            }
        }

        IfId nextIfId;
        var fetchView = default(StageView);
        if (redirect)
        {
            // A redirect outranks a stall: the instructions in ID and IF are from the wrong path,
            // so whether one of them was waiting no longer matters. Both are thrown away, and so
            // is an end of the code that fetch may have run into on that path. A redirect from
            // MEM outranks one from EX, whose instruction it has just thrown away as well.
            nextIdEx = default;
            nextIfId = default;
            _heldFetch = default;
            _pendingEnd = null;
            _pc = redirectTo;

            if (decodeView.HasInstruction)
            {
                decodeView = decodeView with { State = Occupancy.Squashed };
                _events?.Add(new FlushEvent(decodeView.Seq, flushCause, Stage.Decode, redirectBy));
            }

            if (fetching.Valid)
            {
                fetchView = new StageView(Occupancy.Squashed, fetching.Seq, fetching.Pc, fetching.Raw);
                _events?.Add(new FlushEvent(fetching.Seq, flushCause, Stage.Fetch, redirectBy));
            }
        }
        else
        {
            // A stall in ID holds everything behind it: IF/ID keeps its instruction, and so does IF.
            nextIfId = stall ? _ifId : fetching;
            _heldFetch = stall ? fetching : default;
            if (fetching.Valid)
            {
                fetchView = new StageView(stall ? Occupancy.Held : Occupancy.Normal, fetching.Seq, fetching.Pc, fetching.Raw);
                if (!stall)
                {
                    _pc = fetching.Pc + 4;
                }
            }
        }

        // ---- The clock edge: every latch takes its new value at once ----------------------------
        _memWb = nextMemWb;
        _exMem = nextExMem;
        _idEx = nextIdEx;
        _ifId = nextIfId;

        // A run that reaches the end of its code is over when the last instruction has left WB.
        Commit? end = null;
        if (_pendingEnd is { } pending
            && !(_heldFetch.Valid || _ifId.Valid || _idEx.Valid || _exMem.Valid || _memWb.Valid))
        {
            end = pending;
            Stopped = pending.Stop;
        }

        hart.Cycle++;
        var events = _events;
        _events = null;
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
            Events = events ?? (IReadOnlyList<PipelineEvent>)[],
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
    private uint Forward(Operand operand, int register, bool used, uint fromDecode)
    {
        if (!used || register == 0 || Config.Hazards != HazardHandling.Forwarding)
        {
            return fromDecode;
        }

        if (_exMem.Valid && _exMem.Instruction.Control is { WritesRd: true } producer && _exMem.Instruction.Rd == register)
        {
            // In MEM an arithmetic result or a link address is already known. A value that only
            // exists after MEM (a load, a CSR read) is not, and nothing older may stand in for
            // it. That case never gets here: a use of a load has been stalled a cycle, and
            // whatever is behind a CSR instruction is being flushed this very cycle.
            uint? known = producer.Wb switch
            {
                WbSrc.Alu => _exMem.Alu,
                WbSrc.PcPlus4 => _exMem.Pc + 4,
                _ => null,
            };
            if (known is { } value)
            {
                _events?.Add(new ForwardEvent(_idEx.Seq, ForwardSource.ExMem, operand, (byte)register, value, _exMem.Seq));
                return value;
            }

            return fromDecode;
        }

        if (_memWb.Valid && _memWb.Commit.Register == register)
        {
            var value = _memWb.Commit.Value;
            _events?.Add(new ForwardEvent(_idEx.Seq, ForwardSource.MemWb, operand, (byte)register, value, _memWb.Seq));
            return value;
        }

        return fromDecode;
    }

    /// <summary>
    /// The hazard unit, for the instruction in ID: whether it must wait a cycle, and for what.
    /// The registers are the ones it really reads; zero stands for "none".
    /// </summary>
    private (StallCause Cause, byte Register, ulong Producer, Stage ProducerStage)? WaitsFor(int rs1, int rs2)
    {
        var inExecute = _idEx.Valid && _idEx.Instruction.Control.WritesRd ? _idEx.Instruction.Rd : (byte)0;
        var inMemory = _exMem.Valid && _exMem.Instruction.Control.WritesRd ? _exMem.Instruction.Rd : (byte)0;

        bool Needs(byte register) => register != 0 && (register == rs1 || register == rs2);

        if (Config.Hazards == HazardHandling.Forwarding)
        {
            // The load-use hazard. The instruction ahead, now in EX, is a load: its value will
            // not exist until the end of MEM, one cycle too late for this instruction to take it
            // into EX next cycle. Forwarding cannot reach back in time, so this instruction waits
            // here for a cycle, a bubble goes into EX in its place, and then MEM/WB can forward.
            return _idEx.Instruction.Control.Mem == MemOp.Load && Needs(inExecute)
                ? (StallCause.LoadUse, inExecute, _idEx.Seq, Stage.Execute)
                : null;
        }

        // With no forwarding paths the only way to a value is the register file, so this
        // instruction waits until the one that produces it is in WB, where a write is read in
        // the same cycle. The nearer producer is the one to wait for: it is the newer value.
        if (Needs(inExecute))
        {
            return (StallCause.DataHazard, inExecute, _idEx.Seq, Stage.Execute);
        }

        return Needs(inMemory) ? (StallCause.DataHazard, inMemory, _exMem.Seq, Stage.Memory) : null;
    }

    // The four latches. Each carries the sequence number given at fetch, so that one instruction
    // can be followed from stage to stage, and the address and word that identify it.

    private readonly record struct IfId(bool Valid, ulong Seq, uint Pc, uint Raw);

    private readonly record struct IdEx(bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2);

    private readonly record struct ExMem(
        bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2, uint Alu, bool Taken, uint Target);

    private readonly record struct MemWb(bool Valid, ulong Seq, Commit Commit);
}
