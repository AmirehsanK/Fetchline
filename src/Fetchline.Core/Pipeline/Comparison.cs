using Fetchline.Core.Asm;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;

namespace Fetchline.Core.Pipeline;

/// <summary>How a program ran on a pipeline built one way.</summary>
/// <param name="Stats">The counters of the run, up to where it ended or was cut off.</param>
/// <param name="Stopped">Why the run ended, or <see cref="StopReason.None"/> if it was cut off still running.</param>
/// <param name="Last">The record the run ended with: an exit, a fault, a pause or the end of the code.</param>
/// <param name="Divergence">The first instruction on which the pipeline differed from the reference machine, if it did.</param>
public sealed record ComparisonRow(
    PipelineConfig Config, PipelineStats Stats, StopReason Stopped, Commit? Last, Divergence? Divergence)
{
    /// <summary>The pipeline did what the reference machine did, for as long as it ran.</summary>
    public bool IsRight => Divergence is null;

    /// <summary>The run was not cut off for taking too long.</summary>
    public bool Ended => Stopped != StopReason.None;
}

/// <summary>
/// One program on the pipeline built several ways. Each run has the reference machine beside
/// it, so a row says not only how long a configuration took but whether it was right.
/// </summary>
public static class Comparison
{
    /// <summary>
    /// The configurations a comparison covers: every combination of the hazard handlings, branch
    /// decisions and predictors given, in the order a table lists them. A list left out means
    /// every value of that switch. The switches that have no short list of values, the size of
    /// the branch target buffer and the length of a multiply, are taken from
    /// <paramref name="basis"/> and are the same in every row.
    /// </summary>
    public static IReadOnlyList<PipelineConfig> Configurations(
        PipelineConfig? basis = null,
        IReadOnlyList<HazardHandling>? hazards = null,
        IReadOnlyList<BranchDecision>? branches = null,
        IReadOnlyList<Predictor>? predictors = null)
    {
        basis ??= PipelineConfig.Default;
        hazards ??= Enum.GetValues<HazardHandling>();
        branches ??= Enum.GetValues<BranchDecision>();
        predictors ??= Enum.GetValues<Predictor>();

        var all = new List<PipelineConfig>(hazards.Count * branches.Count * predictors.Count);
        foreach (var hazard in hazards)
        {
            foreach (var branch in branches)
            {
                foreach (var predictor in predictors)
                {
                    all.Add(basis with { Hazards = hazard, Branches = branch, Predictor = predictor });
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Runs a program on a pipeline built one way, in lockstep with the reference machine, until
    /// it ends, pauses at an <c>ebreak</c>, or has run for <paramref name="maxCycles"/>. What the
    /// program prints is thrown away. A run that differs from the reference machine carries on
    /// to its own end, which with hazard handling off may never come: hence the limit.
    /// </summary>
    public static ComparisonRow Run(
        Program program, PipelineConfig config, ulong maxCycles, ExecutionEnvironment? environment = null)
    {
        var run = new ComparisonRun(program, config, environment);
        run.Advance(maxCycles);
        return run.Row;
    }
}

/// <summary>
/// One program on a pipeline built one way, run a stretch at a time. A comparison of many
/// configurations in a browser is made of these, so that it can stop between stretches to let
/// the page be drawn, and can be abandoned half way.
/// </summary>
public sealed class ComparisonRun
{
    private readonly Lockstep _lockstep;
    private readonly PipelineStats _stats = new();
    private CycleRecord? _last;

    public ComparisonRun(Program program, PipelineConfig config, ExecutionEnvironment? environment = null)
    {
        Config = config;
        _lockstep = new Lockstep(program, TextWriter.Null, environment, config);
    }

    public PipelineConfig Config { get; }

    /// <summary>The run has ended, or paused at an <c>ebreak</c>, which for a comparison is the end.</summary>
    public bool HasEnded => _lockstep.Pipeline.Stopped != StopReason.None;

    /// <summary>How many cycles have been run. Counted here: a program may write its own counter.</summary>
    public ulong Cycles => _stats.Cycles;

    /// <summary>Runs for up to so many more cycles, and says how many it ran.</summary>
    public ulong Advance(ulong cycles)
    {
        ulong ran = 0;
        for (; ran < cycles && !HasEnded; ran++)
        {
            _last = _lockstep.Step();
            _stats.Add(_last);
        }

        return ran;
    }

    /// <summary>How the run stands: its counters so far, and whether it has gone wrong.</summary>
    public ComparisonRow Row =>
        new(Config, _stats, _lockstep.Pipeline.Stopped, _last?.End ?? _last?.Commit, _lockstep.Divergence);
}
