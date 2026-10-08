using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz;

/// <summary>
/// A comparison in the making: one program to be run on each of several configurations, a
/// stretch at a time. The playground does a stretch, draws the rows it has, and does another,
/// so a long program fills its table in row by row and can be given up on at any point.
/// </summary>
public sealed class CompareJob
{
    private readonly Program _program;
    private readonly IReadOnlyList<PipelineConfig> _configurations;
    private readonly List<ComparisonRow> _rows = [];
    private ComparisonRun? _running;

    /// <param name="mostCycles">
    /// How long a configuration is given before it is cut off as still running. With hazard
    /// handling off a program may never end, and a long one built a slow way may simply take long.
    /// </param>
    public CompareJob(Program program, IReadOnlyList<PipelineConfig> configurations, ulong mostCycles)
    {
        _program = program;
        _configurations = configurations;
        MostCycles = mostCycles;
    }

    public ulong MostCycles { get; }

    /// <summary>How many configurations there are to run.</summary>
    public int Total => _configurations.Count;

    /// <summary>The rows that are finished, in the order of the configurations.</summary>
    public IReadOnlyList<ComparisonRow> Rows => _rows;

    /// <summary>Every configuration has been run to its end or cut off.</summary>
    public bool IsDone => _rows.Count == Total;

    /// <summary>The row being run, as far as it has got; null between rows and when all are done.</summary>
    public ComparisonRow? Running => _running?.Row;

    /// <summary>
    /// Does about so many cycles of work: finishes rows, starts the next, and stops when the
    /// work asked for is done. False when there is nothing left to do.
    /// </summary>
    public bool Advance(ulong cycles)
    {
        while (cycles > 0 && !IsDone)
        {
            _running ??= new ComparisonRun(_program, _configurations[_rows.Count]);

            var allowed = Math.Min(cycles, MostCycles - _running.Cycles);
            cycles -= Math.Max(1, _running.Advance(allowed));
            if (_running.HasEnded || _running.Cycles >= MostCycles)
            {
                _rows.Add(_running.Row);
                _running = null;
            }
        }

        return !IsDone;
    }
}
