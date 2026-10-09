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

    private readonly BranchPredictor _predictor;

    // The caches, when the pipeline has them. They say how long a read takes and nothing about
    // what is read: the data is in the hart's memory either way.
    private readonly Cache? _instructionCache;
    private readonly Cache? _dataCache;

    // How many more cycles the instruction in IF has to wait for its block, and whether it has
    // waited at all: one that has is given its prediction when it arrives, not when it was asked for.
    private int _fetchWait;
    private bool _fetchWaited;

    public PipelineMachine(
        Program program, TextWriter? output = null, ExecutionEnvironment? environment = null, PipelineConfig? config = null)
    {
        Hart = new Hart(program, output, environment);
        Config = config ?? PipelineConfig.Default;
        Config.Validate();
        _predictor = new BranchPredictor(Config);
        _instructionCache = Config.InstructionCache is { } instructions ? new Cache(instructions) : null;
        _dataCache = Config.DataCache is { } data ? new Cache(data) : null;
        _pc = Hart.Pc;
    }

    /// <summary>How this pipeline is built.</summary>
    public PipelineConfig Config { get; }

    /// <summary>The registers, memory and surroundings. Its program counter is not the pipeline's.</summary>
    public Hart Hart { get; }

    /// <summary>
    /// How many cycles have been run. This is the machine's own clock and numbers the records.
    /// The cycle counter a program reads is a CSR, which a program can also write; the two start
    /// together and part company when it does.
    /// </summary>
    public ulong Cycles { get; private set; }

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

        // How the one branch decided this cycle came out, if one was. The predictor learns of it
        // at the clock edge, like any other state, so a lookup in this same cycle does not see it.
        (uint Pc, bool Taken, uint Target)? outcome = null;
        Stopped = StopReason.None;      // a pause at an ebreak ends when the machine is stepped again

        // What is on the wires this cycle, noted by each stage as it works.
        var wires = default(Wires);

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
        var stillInMemory = default(ExMem);
        if (_exMem.Valid && _dataCache is { } dataCache && _exMem.Instruction.Control.Mem != MemOp.None && !_exMem.Looked)
        {
            // The address is looked up as the instruction arrives, before anything knows whether
            // the access is one the machine allows: a miss and a fault are found side by side.
            var access = dataCache.Access(_exMem.Alu);
            _events?.Add(Said(_exMem.Seq, CacheKind.Data, _exMem.Alu, dataCache.Config, access));
            _exMem = _exMem with { Looked = true, Wait = access.Hit ? 0 : dataCache.Config.MissPenalty };
        }

        if (_exMem.Valid && _exMem.Wait > 0)
        {
            // The block is on its way. The instruction keeps MEM, a bubble goes on to WB in its
            // place, and everything behind it waits. Nothing has happened yet: this is the
            // commit point, and it is reached once, when the wait is over.
            stillInMemory = _exMem with { Wait = _exMem.Wait - 1 };
            memoryView = new StageView(Occupancy.Held, _exMem.Seq, _exMem.Pc, _exMem.Instruction.Raw);
            wires = wires with { MemoryAlu = _exMem.Alu, MemoryRs2 = _exMem.Rs2 };
            _events?.Add(new StallEvent(
                _exMem.Seq, StallCause.DataCacheMiss, Stage.Memory, Register: 0, Producer: 0, Remaining: (byte)stillInMemory.Wait));
        }
        else if (_exMem.Valid)
        {
            var control = _exMem.Instruction.Control;
            var commit = hart.Complete(
                _exMem.Pc, _exMem.Instruction, _exMem.Rs1, _exMem.Rs2, _exMem.Alu, _exMem.Taken, _exMem.Target);
            nextMemWb = new MemWb(true, _exMem.Seq, commit);
            memoryView = new StageView(Occupancy.Normal, _exMem.Seq, _exMem.Pc, _exMem.Instruction.Raw);
            wires = wires with { MemoryAlu = _exMem.Alu, MemoryRs2 = _exMem.Rs2 };

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
        var stillExecuting = default(IdEx);
        if (_idEx.Valid && flush)
        {
            executeView = new StageView(Occupancy.Squashed, _idEx.Seq, _idEx.Pc, _idEx.Instruction.Raw);
            _events?.Add(new FlushEvent(_idEx.Seq, flushCause, Stage.Execute, redirectBy));
        }
        else if (_idEx.Valid && stillInMemory.Valid)
        {
            // MEM is waiting, so EX keeps its instruction and does nothing with it yet, except
            // this: it takes its forwarded operands now. The instruction in WB that one of them
            // may come from will be gone long before EX is let go, and what ID read is too old.
            var instruction = _idEx.Instruction;
            var control = instruction.Control;
            var (rs1, rs2) = (_idEx.Rs1, _idEx.Rs2);
            if (!_idEx.Decided && _idEx.Spent == 0)
            {
                rs1 = Forward(Operand.A, instruction.Rs1, control.UsesRs1, rs1);
                rs2 = Forward(Operand.B, instruction.Rs2, control.UsesRs2, rs2);
            }

            stillExecuting = _idEx with { Rs1 = rs1, Rs2 = rs2 };
            executeView = new StageView(Occupancy.Held, _idEx.Seq, _idEx.Pc, instruction.Raw);
            wires = wires with
            {
                ExecuteRs1 = rs1,
                ExecuteRs2 = rs2,
                AluA = Exec.OperandA(control.SrcA, rs1, _idEx.Pc),
                AluB = Exec.OperandB(control.SrcB, rs2, instruction.Imm),
            };
        }
        else if (_idEx.Valid)
        {
            var instruction = _idEx.Instruction;
            var control = instruction.Control;

            // Two kinds of instruction have their operands already and read nothing here: a
            // branch that was decided in ID took them there, and a multiply or divide that has
            // been in EX for a cycle took them when it started.
            var decided = _idEx.Decided;
            var (rs1, rs2) = (_idEx.Rs1, _idEx.Rs2);
            if (!decided && _idEx.Spent == 0)
            {
                // The values read in ID may be out of date: an instruction one or two ahead may
                // have computed a newer one that is not in the register file yet.
                rs1 = Forward(Operand.A, instruction.Rs1, control.UsesRs1, rs1);
                rs2 = Forward(Operand.B, instruction.Rs2, control.UsesRs2, rs2);
            }

            var operandA = Exec.OperandA(control.SrcA, rs1, _idEx.Pc);
            var operandB = Exec.OperandB(control.SrcB, rs2, instruction.Imm);
            wires = wires with { ExecuteRs1 = rs1, ExecuteRs2 = rs2, AluA = operandA, AluB = operandB };

            var remaining = control.IsMulDiv ? Config.MulDivCycles - 1 - _idEx.Spent : 0;
            if (remaining > 0)
            {
                // A multiply or divide that takes several cycles and has not had them all. It
                // keeps EX, a bubble goes on to MEM in its place, and everything behind it
                // waits. Nothing is missing and nothing is on a wrong path: this is a stall that
                // is not a hazard. The operands are kept as they are now, as a multiplier
                // latches its inputs when it starts: by its last cycle the instructions they
                // were forwarded from have left the pipeline.
                stillExecuting = _idEx with { Rs1 = rs1, Rs2 = rs2, Spent = _idEx.Spent + 1 };
                executeView = new StageView(Occupancy.Held, _idEx.Seq, _idEx.Pc, instruction.Raw);
                _events?.Add(new StallEvent(
                    _idEx.Seq, StallCause.MultiCycle, Stage.Execute, Register: 0, Producer: 0, Remaining: (byte)remaining));
            }
            else
            {
                var alu = Exec.Alu(control.Alu, operandA, operandB);
                wires = wires with { AluOut = alu };
                var taken = decided ? _idEx.Taken : Exec.Taken(control.Branch, rs1, rs2);
                var target = decided ? _idEx.Target : Exec.Target(control.Branch, _idEx.Pc, instruction.Imm, alu);

                if (Config.Branches == BranchDecision.Execute)
                {
                    // Fetch went on behind this instruction to wherever it was predicted to lead.
                    // If that is not where it really leads, the two instructions fetched since
                    // are from the wrong path. This is asked of every instruction, not only of
                    // branches: a stale entry in the branch target buffer can send fetch off
                    // after anything.
                    var next = taken ? target : _idEx.Pc + 4;
                    var mispredicted = next != _idEx.PredictedNext;
                    if (mispredicted)
                    {
                        redirect = true;
                        redirectTo = next;
                        redirectBy = _idEx.Seq;
                        flushCause = FlushCause.Branch;
                    }

                    if (control.IsControlFlow || mispredicted)
                    {
                        _events?.Add(new BranchEvent(_idEx.Seq, taken, target, mispredicted, Stage.Execute));
                    }

                    if (control.IsControlFlow)
                    {
                        outcome = (_idEx.Pc, taken, target);
                    }
                }

                nextExMem = new ExMem(true, _idEx.Seq, _idEx.Pc, instruction, rs1, rs2, alu, taken, target);
                executeView = new StageView(Occupancy.Normal, _idEx.Seq, _idEx.Pc, instruction.Raw);
            }
        }

        // ---- ID: decode, and read the source registers ----------------------------------------
        // WB has already written this cycle, so a value written now is the value read now.
        var nextIdEx = default(IdEx);
        var decodeView = default(StageView);
        var stall = stillExecuting.Valid || stillInMemory.Valid;
        var decodeRedirect = false;
        uint decodeTarget = 0;
        if (stall)
        {
            // EX is keeping its instruction, or MEM is and EX has none: either way ID keeps its
            // own and does nothing with it yet.
            nextIdEx = stillExecuting;
            if (_ifId.Valid)
            {
                decodeView = new StageView(Occupancy.Held, _ifId.Seq, _ifId.Pc, _ifId.Raw);
                var waiting = Decoder.Decode(_ifId.Raw);
                wires = wires with { DecodeRs1 = x[waiting.Rs1], DecodeRs2 = x[waiting.Rs2] };
            }
        }
        else if (_ifId.Valid)
        {
            var instruction = Decoder.Decode(_ifId.Raw);
            var control = instruction.Control;

            // A dependency counts only if the instruction really reads the register.
            var rs1 = control.UsesRs1 ? instruction.Rs1 : 0;
            var rs2 = control.UsesRs2 ? instruction.Rs2 : 0;
            var decidesHere = Config.Branches == BranchDecision.Decode && control.IsControlFlow;

            // An instruction that is about to be thrown away waits for nothing and decides nothing.
            var wait = redirect ? null : decidesHere ? ComparatorWaitsFor(rs1, rs2) : WaitsFor(rs1, rs2);
            if (wait is { } waiting)
            {
                stall = true;
                _events?.Add(new StallEvent(
                    _ifId.Seq, waiting.Cause, Stage.Decode, waiting.Register, waiting.Producer, waiting.ProducerStage));
            }
            else if (decidesHere && !redirect)
            {
                // The branch is decided here, a stage early, by a comparator of its own. That
                // saves one of the two squashed instructions, and costs this: the operands are
                // needed a stage early too, so a result still in EX has to be waited for.
                var a = ComparatorOperand(Operand.A, rs1);
                var b = ComparatorOperand(Operand.B, rs2);
                var sum = Exec.Alu(
                    control.Alu,
                    Exec.OperandA(control.SrcA, a, _ifId.Pc),
                    Exec.OperandB(control.SrcB, b, instruction.Imm));
                var taken = Exec.Taken(control.Branch, a, b);
                var target = Exec.Target(control.Branch, _ifId.Pc, instruction.Imm, sum);

                var next = taken ? target : _ifId.Pc + 4;
                decodeRedirect = next != _ifId.PredictedNext;
                decodeTarget = next;
                outcome = (_ifId.Pc, taken, target);
                _events?.Add(new BranchEvent(_ifId.Seq, taken, target, decodeRedirect, Stage.Decode));
                nextIdEx = new IdEx(
                    true, _ifId.Seq, _ifId.Pc, instruction, a, b, _ifId.PredictedNext, Decided: true, taken, target);
            }
            else if (!redirect && Config.Branches == BranchDecision.Decode && _ifId.PredictedNext != _ifId.Pc + 4)
            {
                // Fetch followed a prediction from something that turns out not to be a branch:
                // a stale entry in the branch target buffer. It is put right here, where a
                // branch would have been decided.
                decodeRedirect = true;
                decodeTarget = _ifId.Pc + 4;
                _events?.Add(new BranchEvent(_ifId.Seq, Taken: false, decodeTarget, Mispredicted: true, Stage.Decode));
            }

            if (!stall && !nextIdEx.Valid)
            {
                nextIdEx = new IdEx(
                    true, _ifId.Seq, _ifId.Pc, instruction, x[instruction.Rs1], x[instruction.Rs2], _ifId.PredictedNext);
            }

            decodeView = new StageView(stall ? Occupancy.Held : Occupancy.Normal, _ifId.Seq, _ifId.Pc, _ifId.Raw);
            wires = nextIdEx.Valid
                ? wires with { DecodeRs1 = nextIdEx.Rs1, DecodeRs2 = nextIdEx.Rs2 }
                : wires with { DecodeRs1 = x[instruction.Rs1], DecodeRs2 = x[instruction.Rs2] };
        }

        // ---- IF: read the next instruction ---------------------------------------------------
        var fetching = _heldFetch;
        if (!fetching.Valid && _pendingEnd is null && fetchingAllowed)
        {
            if (hart.TryFetch(_pc, out var word, out var stop))
            {
                // Where to fetch from next is guessed now, from the address and the word alone.
                fetching = new IfId(true, _nextSeq++, _pc, word, _predictor.Predict(_pc, word));
                if (_instructionCache is { } instructionCache)
                {
                    var access = instructionCache.Access(_pc);
                    _events?.Add(Said(fetching.Seq, CacheKind.Instruction, _pc, instructionCache.Config, access));
                    _fetchWait = access.Hit ? 0 : instructionCache.Config.MissPenalty;
                    _fetchWaited = !access.Hit;
                }
            }
            else
            {
                _pendingEnd = stop;
            }
        }

        IfId nextIfId;
        var fetchView = default(StageView);
        var nextPcFrom = NextPcFrom.Held;
        if (redirect)
        {
            // A redirect outranks a stall: the instructions in ID and IF are from the wrong path,
            // so whether one of them was waiting no longer matters. Both are thrown away, and so
            // is an end of the code that fetch may have run into on that path. A redirect from
            // MEM outranks one from EX, whose instruction it has just thrown away as well.
            nextIdEx = default;
            nextIfId = default;
            _heldFetch = default;
            (_fetchWait, _fetchWaited) = (0, false);
            _pendingEnd = null;
            _pc = redirectTo;
            nextPcFrom = flush ? NextPcFrom.Memory : NextPcFrom.Execute;

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
        else if (decodeRedirect)
        {
            // The branch in ID is taken. It goes on to EX itself; only the one instruction
            // fetched behind it is from the wrong path.
            nextIfId = default;
            _heldFetch = default;
            (_fetchWait, _fetchWaited) = (0, false);
            _pendingEnd = null;
            _pc = decodeTarget;
            nextPcFrom = NextPcFrom.Decode;

            if (fetching.Valid)
            {
                fetchView = new StageView(Occupancy.Squashed, fetching.Seq, fetching.Pc, fetching.Raw);
                _events?.Add(new FlushEvent(fetching.Seq, FlushCause.Branch, Stage.Fetch, _ifId.Seq));
            }
        }
        else if (fetching.Valid && _fetchWait > 0)
        {
            // The instruction's block is on its way. It keeps IF and nothing goes on to ID in
            // its place, but nothing else waits for it: what is ahead of it carries on. The
            // wait goes on through a stall, since memory does not know there is one.
            _fetchWait--;
            nextIfId = stall ? _ifId : default;
            _heldFetch = fetching;
            fetchView = new StageView(Occupancy.Held, fetching.Seq, fetching.Pc, fetching.Raw);
            _events?.Add(new StallEvent(
                fetching.Seq, StallCause.InstructionCacheMiss, Stage.Fetch, Register: 0, Producer: 0, Remaining: (byte)_fetchWait));
        }
        else
        {
            // Where fetch goes next is guessed when the word arrives, so an instruction that
            // waited for its block is given the guess the predictor makes now.
            if (fetching.Valid && _fetchWaited && !stall)
            {
                fetching = fetching with { PredictedNext = _predictor.Predict(fetching.Pc, fetching.Raw) };
                _fetchWaited = false;
            }

            // A stall in ID holds everything behind it: IF/ID keeps its instruction, and so does IF.
            nextIfId = stall ? _ifId : fetching;
            _heldFetch = stall ? fetching : default;
            if (fetching.Valid)
            {
                fetchView = new StageView(stall ? Occupancy.Held : Occupancy.Normal, fetching.Seq, fetching.Pc, fetching.Raw);
                if (!stall)
                {
                    _pc = fetching.PredictedNext;
                    nextPcFrom = _pc == fetching.Pc + 4 ? NextPcFrom.Sequential : NextPcFrom.Predicted;
                }
            }
        }

        // ---- The clock edge: every latch takes its new value at once ----------------------------
        _memWb = nextMemWb;
        _exMem = stillInMemory.Valid ? stillInMemory : nextExMem;
        _idEx = nextIdEx;
        _ifId = nextIfId;
        if (outcome is { } decidedBranch)
        {
            _predictor.Update(decidedBranch.Pc, decidedBranch.Taken, decidedBranch.Target);
        }

        // A run that reaches the end of its code is over when the last instruction has left WB.
        Commit? end = null;
        if (_pendingEnd is { } pending
            && !(_heldFetch.Valid || _ifId.Valid || _idEx.Valid || _exMem.Valid || _memWb.Valid))
        {
            end = pending;
            Stopped = pending.Stop;
        }

        hart.Cycle++;
        Cycles++;
        var events = _events;
        _events = null;
        return new CycleRecord
        {
            Cycle = Cycles,
            Fetch = fetchView,
            Decode = decodeView,
            Execute = executeView,
            Memory = memoryView,
            WriteBack = writeBackView,
            Commit = committed,
            End = end,
            Events = events ?? (IReadOnlyList<PipelineEvent>)[],
            Wires = wires with { NextPc = _pc, NextPcFrom = nextPcFrom },
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

    /// <summary>An access to a cache as an event, with blocks named by where they begin.</summary>
    private static CacheEvent Said(ulong seq, CacheKind kind, uint address, CacheConfig config, CacheAccess access) => new(
        seq, kind, address, access.Hit, access.Set, access.Way, config.AddressOf(access.Tag, access.Set), access.Evicted,
        access.Evicted ? config.AddressOf(access.EvictedTag, access.Set) : 0);

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

        if (Config.Hazards == HazardHandling.Off)
        {
            // Nobody is watching: the instruction goes on with whatever it read.
            return null;
        }

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

    /// <summary>
    /// The hazard unit, for a branch that is decided in ID. Its comparator needs the operands
    /// now, a stage earlier than the ALU would, so it has less to choose from.
    /// </summary>
    private (StallCause Cause, byte Register, ulong Producer, Stage ProducerStage)? ComparatorWaitsFor(int rs1, int rs2)
    {
        if (Config.Hazards != HazardHandling.Forwarding)
        {
            // Stalling only, or nothing at all: the rule is the same for every instruction.
            return WaitsFor(rs1, rs2);
        }

        var inExecute = _idEx.Valid && _idEx.Instruction.Control.WritesRd ? _idEx.Instruction.Rd : (byte)0;
        var inMemory = _exMem.Valid && _exMem.Instruction.Control.WritesRd ? _exMem.Instruction.Rd : (byte)0;

        bool Needs(byte register) => register != 0 && (register == rs1 || register == rs2);

        // A result being computed in EX this cycle does not exist yet. One cycle on it will be
        // in EX/MEM and can be forwarded here, unless it is a load, which has to finish MEM
        // first: then it is waited for again below, and is read from the register file after.
        if (Needs(inExecute))
        {
            return (StallCause.BranchOperand, inExecute, _idEx.Seq, Stage.Execute);
        }

        return Needs(inMemory) && ValueInExMem() is null
            ? (StallCause.BranchOperand, inMemory, _exMem.Seq, Stage.Memory)
            : null;
    }

    /// <summary>
    /// An operand of a branch decided in ID: forwarded from EX/MEM when the instruction there
    /// has just computed it, and otherwise read from the register file, which WB has already
    /// written this cycle.
    /// </summary>
    private uint ComparatorOperand(Operand operand, int register)
    {
        if (register != 0 && Config.Hazards == HazardHandling.Forwarding
            && _exMem.Valid && _exMem.Instruction.Control.WritesRd && _exMem.Instruction.Rd == register
            && ValueInExMem() is { } value)
        {
            _events?.Add(new ForwardEvent(_ifId.Seq, ForwardSource.ExMem, operand, (byte)register, value, _exMem.Seq, Stage.Decode));
            return value;
        }

        return Hart.X[register];
    }

    /// <summary>
    /// The result of the instruction in MEM, if it is one that EX already produced: an
    /// arithmetic result or a link address. What a load or a CSR read will give is not known yet.
    /// </summary>
    private uint? ValueInExMem() => _exMem.Instruction.Control.Wb switch
    {
        WbSrc.Alu => _exMem.Alu,
        WbSrc.PcPlus4 => _exMem.Pc + 4,
        _ => null,
    };

    // The four latches. Each carries the sequence number given at fetch, so that one instruction
    // can be followed from stage to stage, and the address and word that identify it.

    /// <param name="PredictedNext">Where fetch went after this instruction, on the predictor's word.</param>
    private readonly record struct IfId(bool Valid, ulong Seq, uint Pc, uint Raw, uint PredictedNext);

    /// <param name="Decided">The branch was decided in ID; its outcome and target travel with it.</param>
    /// <param name="Spent">
    /// The cycles the instruction has already had in EX. It is only ever above zero for a multiply
    /// or divide that takes several, and from then on <c>Rs1</c> and <c>Rs2</c> are its operands
    /// as they were forwarded in its first cycle.
    /// </param>
    private readonly record struct IdEx(
        bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2, uint PredictedNext,
        bool Decided = false, bool Taken = false, uint Target = 0, int Spent = 0);

    /// <param name="Looked">The data cache has been asked for the instruction's address.</param>
    /// <param name="Wait">How many more cycles it has to wait in MEM for its block.</param>
    private readonly record struct ExMem(
        bool Valid, ulong Seq, uint Pc, Instruction Instruction, uint Rs1, uint Rs2, uint Alu, bool Taken, uint Target,
        bool Looked = false, int Wait = 0);

    private readonly record struct MemWb(bool Valid, ulong Seq, Commit Commit);
}
