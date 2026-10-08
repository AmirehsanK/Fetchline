using Fetchline.Core.Pipeline;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using Fetchline.Viz.Explain;

namespace Fetchline.Tests.Viz;

/// <summary>The what-if switches as the playground offers them.</summary>
public class SwitchBoardTests
{
    private static readonly IMessages Messages = EnglishMessages.Instance;

    private static string Shape(PipelineConfig config) => string.Join(
        " | ",
        SwitchBoard.Of(config, Messages).Select(group =>
            group.Title + ": " + string.Join(' ', group.Options.Select(option => option.Chosen ? $"[{option.Name}]" : option.Name))));

    [Fact]
    public void TheTextbookPipelineHasEverySwitchInItsFirstPosition()
    {
        Assert.Equal(
            "hazards: [forwarding] stall off | branch: [ex] id | predictor: [not-taken] backward-taken 1-bit 2-bit | muldiv: [1] 3",
            Shape(PipelineConfig.Default));
    }

    [Fact]
    public void TheBufferIsOfferedOnlyWhileThePredictorHasOne()
    {
        var twoBit = new PipelineConfig { Predictor = Predictor.TwoBit };

        Assert.Equal(
            "hazards: [forwarding] stall off | branch: [ex] id | predictor: not-taken backward-taken 1-bit [2-bit] | " +
            "btb: 16 [64] 256 | muldiv: [1] 3",
            Shape(twoBit));
        Assert.Contains("btb: [16] 64 256", Shape(twoBit with { Predictor = Predictor.OneBit, BtbEntries = 16 }));
        Assert.DoesNotContain("btb", Shape(twoBit with { Predictor = Predictor.BackwardTaken }));

        // The size chosen is still there after a spell with a predictor that has no use for it.
        var small = twoBit with { BtbEntries = 16 };
        var without = SwitchBoard.Of(small, Messages)[2].Options.Single(option => option.Name == "not-taken").Gives;
        var again = SwitchBoard.Of(without, Messages)[2].Options.Single(option => option.Name == "2-bit").Gives;
        Assert.Equal(small, again);
    }

    [Fact]
    public void APositionChangesItsOwnSwitchAndNoOther()
    {
        var config = new PipelineConfig
        {
            Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, Predictor = Predictor.OneBit, BtbEntries = 256, MulDivCycles = 3,
        };

        Assert.Equal("hazards: forwarding [stall] off | branch: ex [id] | predictor: not-taken backward-taken [1-bit] 2-bit | btb: 16 64 [256] | muldiv: 1 [3]", Shape(config));

        foreach (var group in SwitchBoard.Of(config, Messages))
        {
            // The position it is in gives the configuration it has; every other gives another,
            // and exactly one position of the group is the one chosen.
            Assert.Equal(config, group.Options.Single(option => option.Chosen).Gives);
            Assert.All(group.Options.Where(option => !option.Chosen), option => Assert.NotEqual(config, option.Gives));
            Assert.Equal(group.Options.Count, group.Options.Select(option => option.Gives).Distinct().Count());
        }

        var slower = SwitchBoard.Of(config, Messages)[0].Options[0].Gives;
        Assert.Equal(config with { Hazards = HazardHandling.Forwarding }, slower);
    }

    [Fact]
    public void WhatCanBeSwitchedToIsExactlyWhatTheTestsRunEverythingOn()
    {
        // Every configuration that can be reached by flipping switches, starting anywhere.
        var reached = new HashSet<PipelineConfig> { PipelineConfig.Default };
        var waiting = new Queue<PipelineConfig>([PipelineConfig.Default]);
        while (waiting.TryDequeue(out var config))
        {
            foreach (var option in SwitchBoard.Of(config, Messages).SelectMany(group => group.Options))
            {
                if (reached.Add(option.Gives))
                {
                    waiting.Enqueue(option.Gives);
                }
            }
        }

        // None of them is one the machine would refuse.
        Assert.All(reached, config => config.Validate());

        // A predictor with no table behaves the same whatever size the table would have been,
        // so those are counted once. What is left is the 64 correct configurations the tests
        // hold to lockstep on 2,000 random programs, and the same 32 over again with hazard
        // handling off.
        static PipelineConfig Same(PipelineConfig config) =>
            config.Predictor is Predictor.OneBit or Predictor.TwoBit ? config : config with { BtbEntries = 64 };

        var distinct = reached.Select(Same).ToHashSet();
        Assert.Equal(64 + 32, distinct.Count);
        Assert.Equal(Configurations.Correct.ToHashSet(), distinct.Where(config => config.IsCorrect).ToHashSet());
        Assert.Equal(32, distinct.Count(config => !config.IsCorrect));
    }

    [Fact]
    public void ASwitchIsNamedAsTheCommandLineNamesIt()
    {
        var groups = SwitchBoard.Of(new PipelineConfig { Predictor = Predictor.TwoBit }, Messages);

        // "--hazards stall", "--btb 256": the title is the option and the position its value.
        Assert.Equal(["hazards", "branch", "predictor", "btb", "muldiv"], groups.Select(group => group.Title));
        Assert.Equal(SwitchNames.All<HazardHandling>(SwitchNames.Of), groups[0].Options.Select(option => option.Name));
        Assert.Equal(SwitchNames.All<BranchDecision>(SwitchNames.Of), groups[1].Options.Select(option => option.Name));
        Assert.Equal(SwitchNames.All<Predictor>(SwitchNames.Of), groups[2].Options.Select(option => option.Name));
        Assert.Equal(["16", "64", "256"], groups[3].Options.Select(option => option.Name));
        Assert.Equal(["1", "3"], groups[4].Options.Select(option => option.Name));
    }
}
