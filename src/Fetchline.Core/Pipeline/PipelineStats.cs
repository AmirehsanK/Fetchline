using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>
/// The counters of a run: cycles, instructions, and how often each thing happened. They are
/// counted from the cycle records and from nothing else, so a counter and a diagram of the same
/// run can never disagree.
/// </summary>
public sealed class PipelineStats
{
    private readonly int[] _stalls = new int[Enum.GetValues<StallCause>().Length];
    private readonly int[] _flushes = new int[Enum.GetValues<FlushCause>().Length];
    private readonly int[] _forwards = new int[Enum.GetValues<ForwardSource>().Length];

    public ulong Cycles { get; private set; }

    /// <summary>Instructions that completed. One that trapped or faulted did not.</summary>
    public ulong Instructions { get; private set; }

    /// <summary>Cycles per instruction.</summary>
    public double Cpi => Instructions == 0 ? 0 : (double)Cycles / Instructions;

    /// <summary>Cycles in which an instruction was made to wait.</summary>
    public int Stalls => _stalls.Sum();

    /// <summary>Times something was thrown away, however many instructions each time.</summary>
    public int Flushes => _flushes.Sum();

    /// <summary>Instructions that were fetched and then thrown away.</summary>
    public int Squashed { get; private set; }

    /// <summary>Operands taken from a latch instead of the register file.</summary>
    public int Forwards => _forwards.Sum();

    /// <summary>Branches and jumps decided.</summary>
    public int Branches { get; private set; }

    public int BranchesTaken { get; private set; }

    /// <summary>Branches and jumps that fetch had not followed correctly.</summary>
    public int Mispredictions { get; private set; }

    public int Loads { get; private set; }

    public int Stores { get; private set; }

    public int Traps { get; private set; }

    public static PipelineStats Of(IEnumerable<CycleRecord> records)
    {
        var stats = new PipelineStats();
        foreach (var record in records)
        {
            stats.Add(record);
        }

        return stats;
    }

    public int StallsBy(StallCause cause) => _stalls[(int)cause];

    public int FlushesBy(FlushCause cause) => _flushes[(int)cause];

    public int ForwardsFrom(ForwardSource source) => _forwards[(int)source];

    public void Add(CycleRecord record)
    {
        Cycles++;
        if (record.Commit is { Trapped: false, Stop: not StopReason.Fault })
        {
            Instructions++;
        }

        FlushCause? flushed = null;
        foreach (var item in record.Events)
        {
            switch (item)
            {
                case StallEvent stall:
                    _stalls[(int)stall.Cause]++;
                    break;
                case ForwardEvent forward:
                    _forwards[(int)forward.From]++;
                    break;
                case FlushEvent flush:
                    Squashed++;
                    flushed = flush.Cause;
                    break;
                case BranchEvent branch:
                    Branches++;
                    BranchesTaken += branch.Taken ? 1 : 0;
                    Mispredictions += branch.Mispredicted ? 1 : 0;
                    break;
                case MemReadEvent:
                    Loads++;
                    break;
                case MemWriteEvent:
                    Stores++;
                    break;
                case TrapEvent:
                    Traps++;
                    break;
            }
        }

        // One redirect squashes up to three instructions; it is still one flush.
        if (flushed is { } cause)
        {
            _flushes[(int)cause]++;
        }
    }
}
