using Fetchline.Core.Asm;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;

namespace Fetchline.Viz;

/// <summary>
/// What is behind the playground's screen: a source text, the program it assembles to, and a
/// run of that program on the pipeline that can be stepped forwards and backwards. There is no
/// user interface in it, so it is tested like the rest of the engine.
///
/// The registers, the memory and the console it shows are made from the cycle records and from
/// nothing else: a register changes here when a record says a register was written. So stepping
/// back is taking one record's changes out again, and costs the same however long the run has
/// been. Only the most recent records are kept; going back past them is a replay from reset,
/// which gives the same run because the machine has no other inputs.
/// </summary>
public sealed class Session
{
    /// <summary>How many cycles of records are kept unless told otherwise.</summary>
    public const int DefaultHistory = 20_000;

    private readonly int _history;

    // Records are dropped this many at a time, so that a long run is not forever shuffling a list.
    private readonly int _slack;

    private readonly List<CycleRecord> _records = [];
    private readonly List<Change> _changes = [];
    private readonly uint[] _registers = new uint[Core.Isa.Registers.Count];
    private readonly StringWriter _console = new();

    // What each cache holds, by the kind of cache; null for one the pipeline does not have.
    private readonly Lines?[] _caches = new Lines?[2];

    private Lockstep? _lockstep;
    private Memory _memory = new();

    // The cycle of the oldest record still held. The cycle being shown is never one whose
    // record has been dropped: it is this one or later, or it is zero.
    private ulong _first = 1;

    /// <param name="history">How many cycles of records to keep at least; older ones are dropped.</param>
    public Session(string source, PipelineConfig? config = null, int history = DefaultHistory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(history, 1);
        Source = source;
        Config = config ?? PipelineConfig.Default;
        Config.Validate();
        _history = history;
        _slack = Math.Clamp(history / 4, 1, 1024);
        Assembly = Assembler.Assemble(source);
        Restart();
    }

    /// <summary>The text that was assembled.</summary>
    public string Source { get; }

    /// <summary>How the pipeline is built.</summary>
    public PipelineConfig Config { get; }

    /// <summary>What the assembler made of the source: the program, and anything it had to say.</summary>
    public AssemblyResult Assembly { get; }

    /// <summary>The program, or null when the source has errors and there is nothing to run.</summary>
    public Program? Program => Assembly.Program;

    /// <summary>The cycle being shown. Zero is the machine at reset, before any cycle has run.</summary>
    public ulong Cycle { get; private set; }

    /// <summary>The furthest cycle that has been run. Stepping back does not undo the running.</summary>
    public ulong Frontier => _lockstep?.Pipeline.Cycles ?? 0;

    /// <summary>The earliest cycle whose record is still held. Diagrams can begin no earlier.</summary>
    public ulong FirstKept => _first;

    /// <summary>The record of the cycle being shown, or null at reset.</summary>
    public CycleRecord? Record => Cycle == 0 ? null : _records[(int)(Cycle - _first)];

    /// <summary>
    /// Why the run stopped, if the cycle being shown is the one it stopped in:
    /// <see cref="StopReason.Breakpoint"/> is a pause, anything else but
    /// <see cref="StopReason.None"/> is the end.
    /// </summary>
    public StopReason Stopped => _lockstep is { } lockstep && Cycle == Frontier ? lockstep.Pipeline.Stopped : StopReason.None;

    /// <summary>The cycle being shown is the last there will be.</summary>
    public bool IsFinished => Stopped is not (StopReason.None or StopReason.Breakpoint);

    public bool CanStep => Program is not null && !IsFinished;

    public bool CanStepBack => Cycle > 0;

    /// <summary>
    /// The first instruction on which the pipeline differed from the reference machine, once the
    /// run has been stepped that far. With hazard handling off this is expected and is the lesson.
    /// </summary>
    public Divergence? Divergence => _lockstep?.Divergence is { } divergence && divergence.Cycle <= Cycle ? divergence : null;

    /// <summary>
    /// The first wrong value the run has come to so far, even when the cycle being shown is
    /// before it: for a list of what has been run, as <see cref="Recorded"/> is.
    /// </summary>
    public Divergence? DivergenceSoFar => _lockstep?.Divergence;

    /// <summary>The integer registers after the cycle being shown.</summary>
    public ReadOnlySpan<uint> Registers => _registers;

    /// <summary>The counters of the run up to the cycle being shown.</summary>
    public PipelineStats Stats { get; private set; } = new();

    /// <summary>What the program has printed up to the cycle being shown.</summary>
    public string Output
    {
        get
        {
            var length = Cycle == 0 ? 0 : _changes[(int)(Cycle - _first)].ConsoleLength;
            return _console.GetStringBuilder().ToString(0, length);
        }
    }

    /// <summary>
    /// One way of one set of a cache after the cycle being shown: whether it holds a block, the
    /// address the block begins at, and the cycle it was last used in. The cache has to be one
    /// the pipeline has.
    /// </summary>
    public (bool Valid, uint Block, ulong Used) CacheLine(CacheKind kind, int set, int way)
    {
        var lines = _caches[(int)kind] ?? throw new InvalidOperationException("The pipeline has no such cache.");
        var slot = set * lines.Ways + way;
        return (lines.Valid[slot], lines.Block[slot], lines.Used[slot]);
    }

    /// <summary>A byte of memory after the cycle being shown. Memory never written reads as zero.</summary>
    public byte ReadByte(uint address) => _memory.ReadByte(address);

    /// <summary>A little-endian word of memory after the cycle being shown.</summary>
    public uint ReadWord(uint address) => _memory.ReadU32(address);

    /// <summary>
    /// The records of the cycles from <paramref name="from"/> to <paramref name="to"/>, as far as
    /// they are held and have been reached: nothing after the cycle being shown, and nothing
    /// that has been dropped.
    /// </summary>
    public IReadOnlyList<CycleRecord> Records(ulong from, ulong to)
    {
        from = Math.Max(from, _first);
        to = Math.Min(to, Cycle);
        return from > to ? [] : _records.GetRange((int)(from - _first), (int)(to - from + 1));
    }

    /// <summary>
    /// The records of the cycles from <paramref name="from"/> to <paramref name="to"/> that have
    /// been run and are still held, those after the cycle being shown included. A list of what
    /// happened can then offer a way forward again after the run has been stepped back.
    /// </summary>
    public IReadOnlyList<CycleRecord> Recorded(ulong from, ulong to)
    {
        from = Math.Max(from, _first);
        to = Math.Min(to, Frontier);
        return from > to ? [] : _records.GetRange((int)(from - _first), (int)(to - from + 1));
    }

    /// <summary>Goes one cycle forwards. False when the run has ended or there is no program.</summary>
    public bool Step()
    {
        if (_lockstep is not { } lockstep)
        {
            return false;
        }

        if (Cycle < Frontier)
        {
            // Run before and stepped back from: the record is here, and is simply applied again.
            Cycle++;
            Apply(_records[(int)(Cycle - _first)]);
            return true;
        }

        if (lockstep.Pipeline.IsFinished)
        {
            return false;
        }

        var record = lockstep.Step();
        _records.Add(record);
        _changes.Add(ChangesOf(record));
        Cycle++;
        Apply(record);

        if (_records.Count >= _history + _slack)
        {
            _records.RemoveRange(0, _slack);
            _changes.RemoveRange(0, _slack);
            _first += (ulong)_slack;
        }

        return true;
    }

    /// <summary>Goes one cycle back. False at reset.</summary>
    public bool StepBack()
    {
        if (Cycle == 0)
        {
            return false;
        }

        Seek(Cycle - 1);
        return true;
    }

    /// <summary>
    /// Runs forwards for up to <paramref name="maxCycles"/>, stopping early at the end of the run
    /// or at a pause. Returns how many cycles it went. A long run is made of several calls, so
    /// that whatever is drawing the screen gets a turn between them.
    /// </summary>
    public int Run(int maxCycles)
    {
        var ran = 0;
        while (ran < maxCycles && Step())
        {
            ran++;
            if (Stopped == StopReason.Breakpoint)
            {
                break;
            }
        }

        return ran;
    }

    /// <summary>
    /// Shows another cycle: one already run, or one further on, which is run to. A cycle past the
    /// end of the run shows the end.
    /// </summary>
    public void Seek(ulong cycle)
    {
        if (_lockstep is null)
        {
            return;
        }

        // A cycle whose record has been dropped cannot be reached by undoing: there is nothing
        // left to say what it changed. It is run to again, from reset.
        if (_first > 1 && cycle < _first)
        {
            Restart();
        }

        while (Cycle > cycle)
        {
            Undo(_records[(int)(Cycle - _first)], _changes[(int)(Cycle - _first)]);
            Cycle--;
        }

        while (Cycle < cycle && Step())
        {
        }
    }

    /// <summary>Back to the machine at reset.</summary>
    public void Reset() => Seek(0);

    private void Restart()
    {
        _records.Clear();
        _changes.Clear();
        _console.GetStringBuilder().Clear();
        _first = 1;
        Cycle = 0;
        Stats = new PipelineStats();
        Array.Clear(_registers);
        _memory = new Memory();
        _lockstep = null;
        _caches[(int)CacheKind.Instruction] = Config.InstructionCache is { } instructions ? new Lines(instructions) : null;
        _caches[(int)CacheKind.Data] = Config.DataCache is { } data ? new Lines(data) : null;

        if (Program is not { } program)
        {
            return;
        }

        _lockstep = new Lockstep(program, _console, config: Config);

        // The state at reset is the one thing that does not come from a record: the registers
        // the surroundings set up, and the program's own bytes in memory.
        _lockstep.Pipeline.Hart.X.CopyTo(_registers, 0);
        foreach (var segment in program.Segments)
        {
            _memory.WriteBytes(segment.Address, segment.Data.Span);
        }
    }

    /// <summary>
    /// What a cycle is about to overwrite, noted the first time the cycle is run so that it can
    /// be put back. A cycle has one instruction in WB and one in MEM, so it writes at most one
    /// register and stores at most once.
    /// </summary>
    private Change ChangesOf(CycleRecord record)
    {
        var change = new Change { ConsoleLength = _console.GetStringBuilder().Length };
        foreach (var item in record.Events)
        {
            switch (item)
            {
                case RegWriteEvent write:
                    change = change with { Register = write.Register, OldRegister = _registers[write.Register] };
                    break;
                case MemWriteEvent store:
                    change = change with
                    {
                        StoreBytes = store.Bytes,
                        StoreAddress = store.Address,
                        OldStore = _memory.Read(store.Address, store.Bytes),
                    };
                    break;
                case CacheEvent access when _caches[(int)access.Kind] is { } lines:
                    var slot = access.Set * lines.Ways + access.Way;
                    var was = new Line(true, slot, lines.Valid[slot], lines.Block[slot], lines.Used[slot]);
                    change = access.Kind == CacheKind.Instruction ? change with { Instructions = was } : change with { Data = was };
                    break;
            }
        }

        return change;
    }

    private void Apply(CycleRecord record)
    {
        foreach (var item in record.Events)
        {
            switch (item)
            {
                case RegWriteEvent write:
                    _registers[write.Register] = write.Value;
                    break;
                case MemWriteEvent store:
                    _memory.Write(store.Address, store.Bytes, store.Value);
                    break;
                case CacheEvent access when _caches[(int)access.Kind] is { } lines:
                    // Hit or miss, the block is in that way now and has just been used.
                    var slot = access.Set * lines.Ways + access.Way;
                    (lines.Valid[slot], lines.Block[slot], lines.Used[slot]) = (true, access.Block, record.Cycle);
                    break;
            }
        }

        Stats.Add(record);
    }

    private void Undo(CycleRecord record, in Change change)
    {
        if (change.Register != 0)
        {
            _registers[change.Register] = change.OldRegister;
        }

        if (change.StoreBytes != 0)
        {
            _memory.Write(change.StoreAddress, change.StoreBytes, change.OldStore);
        }

        Restore(_caches[(int)CacheKind.Instruction], change.Instructions);
        Restore(_caches[(int)CacheKind.Data], change.Data);
        Stats.Remove(record);

        static void Restore(Lines? lines, in Line was)
        {
            if (lines is not null && was.Touched)
            {
                (lines.Valid[was.Slot], lines.Block[was.Slot], lines.Used[was.Slot]) = (was.Valid, was.Block, was.Used);
            }
        }
    }

    /// <param name="Instructions">The way of the instruction cache the cycle used, as it was before.</param>
    /// <param name="Data">The same for the data cache. A cycle asks each cache at most once.</param>
    private readonly record struct Change(
        byte Register, uint OldRegister, byte StoreBytes, uint StoreAddress, uint OldStore, int ConsoleLength,
        Line Instructions = default, Line Data = default);

    /// <summary>One way of a cache as it was before a cycle used it.</summary>
    private readonly record struct Line(bool Touched, int Slot, bool Valid, uint Block, ulong Used);

    /// <summary>What a cache holds, way by way, as the records have said.</summary>
    private sealed class Lines(CacheConfig config)
    {
        public int Ways { get; } = config.Ways;

        public bool[] Valid { get; } = new bool[config.Sets * config.Ways];

        public uint[] Block { get; } = new uint[config.Sets * config.Ways];

        public ulong[] Used { get; } = new ulong[config.Sets * config.Ways];
    }
}
