using System.Globalization;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Explain;

namespace Fetchline.Viz;

/// <summary>One position of a switch.</summary>
/// <param name="Name">What it is called: the name the command line takes for it.</param>
/// <param name="Chosen">The switch is in this position now.</param>
/// <param name="Gives">The configuration that choosing this position gives.</param>
public sealed record SwitchOption(string Name, bool Chosen, PipelineConfig Gives);

/// <summary>One what-if switch: what it is about, and its positions.</summary>
public sealed record SwitchGroup(string Title, IReadOnlyList<SwitchOption> Options);

/// <summary>
/// The what-if switches as the playground offers them. What it offers is exactly what the tests
/// run everything on: three ways with hazards, two stages to decide a branch in, four
/// predictors, a branch target buffer of 16, 64 or 256 entries, and a multiply of one cycle or
/// three. The buffer is only offered while the predictor is one that has a buffer.
/// </summary>
public static class SwitchBoard
{
    /// <summary>The sizes of branch target buffer on offer.</summary>
    public static IReadOnlyList<int> BufferSizes { get; } = [16, 64, 256];

    /// <summary>The lengths of a multiply or divide on offer, in cycles.</summary>
    public static IReadOnlyList<int> MultiplyCycles { get; } = [1, 3];

    public static IReadOnlyList<SwitchGroup> Of(PipelineConfig config, IMessages messages)
    {
        var groups = new List<SwitchGroup>
        {
            new(messages.HazardsHeading, [.. Enum.GetValues<HazardHandling>().Select(value =>
                new SwitchOption(SwitchNames.Of(value), config.Hazards == value, config with { Hazards = value }))]),
            new(messages.BranchHeading, [.. Enum.GetValues<BranchDecision>().Select(value =>
                new SwitchOption(SwitchNames.Of(value), config.Branches == value, config with { Branches = value }))]),
            new(messages.PredictorHeading, [.. Enum.GetValues<Predictor>().Select(value =>
                new SwitchOption(SwitchNames.Of(value), config.Predictor == value, config with { Predictor = value }))]),
        };

        // A predictor that keeps no table has no table to size. The size chosen before is kept
        // all the same, and is there again when a predictor that uses it is chosen.
        if (config.Predictor is Predictor.OneBit or Predictor.TwoBit)
        {
            groups.Add(new SwitchGroup(messages.BufferHeading, [.. BufferSizes.Select(size =>
                new SwitchOption(Number(size), config.BtbEntries == size, config with { BtbEntries = size }))]));
        }

        groups.Add(new SwitchGroup(messages.MultiplyHeading, [.. MultiplyCycles.Select(cycles =>
            new SwitchOption(Number(cycles), config.MulDivCycles == cycles, config with { MulDivCycles = cycles }))]));
        return groups;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
