using Fetchline.Core.Pipeline;
using Fetchline.Viz;

namespace Fetchline.Tests.Support;

/// <summary>The ways of building the pipeline that the tests run everything on.</summary>
internal static class Configurations
{
    /// <summary>
    /// Every correct configuration the playground offers: forwarding or stalling only, branches
    /// decided in EX or in ID, the two predictors that need no table and the two that do with a
    /// buffer of 16, 64 or 256 entries, and a multiplier of one cycle or of three.
    /// 2 * 2 * (2 + 2 * 3) * 2 = 64.
    /// </summary>
    public static IReadOnlyList<PipelineConfig> Correct { get; } = Build();

    /// <summary>A configuration in a failure message, as the switches that give it.</summary>
    public static string Name(PipelineConfig config) =>
        $"--hazards {SwitchNames.Of(config.Hazards)} --branch {SwitchNames.Of(config.Branches)} " +
        $"--predictor {SwitchNames.Of(config.Predictor)} --btb {config.BtbEntries} --muldiv {config.MulDivCycles}";

    private static List<PipelineConfig> Build()
    {
        var all = new List<PipelineConfig>();
        foreach (var hazards in new[] { HazardHandling.Forwarding, HazardHandling.StallOnly })
        {
            foreach (var branches in Enum.GetValues<BranchDecision>())
            {
                foreach (var predictor in Enum.GetValues<Predictor>())
                {
                    // A predictor without a table is the same whatever size the table would be.
                    int[] sizes = predictor is Predictor.OneBit or Predictor.TwoBit ? [16, 64, 256] : [64];
                    foreach (var entries in sizes)
                    {
                        foreach (var cycles in new[] { 1, 3 })
                        {
                            all.Add(new PipelineConfig
                            {
                                Hazards = hazards,
                                Branches = branches,
                                Predictor = predictor,
                                BtbEntries = entries,
                                MulDivCycles = cycles,
                            });
                        }
                    }
                }
            }
        }

        return all;
    }
}
