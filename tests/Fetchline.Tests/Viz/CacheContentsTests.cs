using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using Fetchline.Viz.State;

namespace Fetchline.Tests.Viz;

/// <summary>What the playground shows a cache to hold: made from the records, like the registers.</summary>
public class CacheContentsTests
{
    private const ulong Seed = 0xCAC4_E200;

    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    /// <summary>Every way of a cache as text: the address its block begins at, or a dash.</summary>
    private static string Inside(Cache cache)
    {
        var config = cache.Config;
        return string.Join(' ', Enumerable.Range(0, config.Sets).SelectMany(set => Enumerable.Range(0, config.Ways).Select(way =>
            cache.Line(set, way) is (true, var tag) ? config.AddressOf(tag, set).ToString("x8") : "-")));
    }

    private static string Shown(Session session, CacheKind kind) =>
        string.Join(' ', CacheContents.Of(session, kind).SelectMany(row => row.Ways.Select(cell => cell.Valid ? cell.Block.ToString("x8") : "-")));

    [Fact]
    public void APipelineWithNoCacheHasNoneToShow()
    {
        var session = new Session("nop");

        Assert.Empty(CacheContents.Kinds(session.Config));
        Assert.Empty(CacheContents.Of(session, CacheKind.Data));
        Assert.Throws<InvalidOperationException>(() => session.CacheLine(CacheKind.Data, 0, 0));

        var both = new PipelineConfig { InstructionCache = new CacheConfig(2, 1, 4, 1), DataCache = new CacheConfig(4, 2, 8, 1) };
        Assert.Equal([CacheKind.Instruction, CacheKind.Data], CacheContents.Kinds(both));
        Assert.Equal(new CacheConfig(4, 2, 8, 1), CacheContents.Shape(both, CacheKind.Data));
        Assert.Empty(CacheContents.Of(new Session("bogus", both), CacheKind.Data));
    }

    [Fact]
    public void TheWayUsedThisCycleIsMarkedAndSoIsTheOneThatGoesNext()
    {
        // One set of two ways. Three loads from three blocks: the third puts the first out.
        var config = new PipelineConfig { DataCache = new CacheConfig(1, 2, 4, 1) };
        var session = new Session("lw t0, 0(sp)\nlw t1, 4(sp)\nlw t0, 0(sp)\nlw t2, 8(sp)\n", config);
        const uint Top = 0x7FFF_FFF0;

        // At reset the cache is empty, and nothing in an empty cache goes next.
        var empty = Assert.Single(CacheContents.Of(session, CacheKind.Data));
        Assert.Equal([new CacheCell(false, 0, CacheTouch.None, false), new CacheCell(false, 0, CacheTouch.None, false)], empty.Ways);

        // The first load reaches MEM in cycle 4 and misses into way 0.
        session.Seek(4);
        Assert.Equal(
            [new CacheCell(true, Top, CacheTouch.Miss, false), new CacheCell(false, 0, CacheTouch.None, false)],
            CacheContents.Of(session, CacheKind.Data)[0].Ways);

        // A cycle later it is waiting and nothing is touched. Then the second load misses into
        // way 1: the set is full, and the first block is the one that would go next.
        session.Seek(5);
        Assert.All(CacheContents.Of(session, CacheKind.Data)[0].Ways, cell => Assert.Equal(CacheTouch.None, cell.Touch));
        session.Seek(6);
        Assert.Equal(
            [new CacheCell(true, Top, CacheTouch.None, true), new CacheCell(true, Top + 4, CacheTouch.Miss, false)],
            CacheContents.Of(session, CacheKind.Data)[0].Ways);

        // The third load hits the first block, which makes the second the older one.
        session.Seek(8);
        Assert.Equal(
            [new CacheCell(true, Top, CacheTouch.Hit, false), new CacheCell(true, Top + 4, CacheTouch.None, true)],
            CacheContents.Of(session, CacheKind.Data)[0].Ways);

        // So the fourth puts the second out, not the first.
        session.Seek(9);
        Assert.Equal(
            [new CacheCell(true, Top, CacheTouch.None, true), new CacheCell(true, Top + 8, CacheTouch.Miss, false)],
            CacheContents.Of(session, CacheKind.Data)[0].Ways);

        // And stepping back puts it in again.
        session.Seek(8);
        Assert.Equal(Top + 4, CacheContents.Of(session, CacheKind.Data)[0].Ways[1].Block);
        session.Seek(3);
        Assert.All(CacheContents.Of(session, CacheKind.Data)[0].Ways, cell => Assert.False(cell.Valid));
    }

    [Fact]
    public void WhatIsShownIsWhatTheMachinesOwnCachesHoldAtEveryCycleForwardsAndBack()
    {
        (CacheConfig Instructions, CacheConfig Data)[] shapes =
        [
            (new CacheConfig(4, 1, 16, 10), new CacheConfig(2, 2, 16, 10)),
            (new CacheConfig(2, 2, 8, 2), new CacheConfig(1, 4, 4, 3)),
            (new CacheConfig(16, 1, 16, 1), new CacheConfig(8, 1, 4, 7)),
        ];
        var sources = new[] { Example("bubble-sort.s"), Example("factorial.s") }
            .Concat(Enumerable.Range(0, 24).Select(index => ProgramGenerator.Generate(new SeededRandom(Seed + (ulong)index)))).ToList();

        for (var index = 0; index < sources.Count; index++)
        {
            var (instructions, data) = shapes[index % shapes.Length];
            var plain = Configurations.Correct[index * 7 % Configurations.Correct.Count];
            var config = plain with { InstructionCache = instructions, DataCache = data };
            var where = $"source {index} with {Configurations.Name(plain)}";

            // The session has only the records. A second machine runs beside it and is looked inside.
            var session = new Session(sources[index], config);
            var machine = new PipelineMachine(AssemblerTesting.Assemble(sources[index]), config: config);
            var seen = new List<(string Instructions, string Data)> { (Inside(machine.InstructionCache!), Inside(machine.DataCache!)) };
            while (session.Step() && seen.Count < 3000)
            {
                machine.Step();
                seen.Add((Inside(machine.InstructionCache!), Inside(machine.DataCache!)));
                Assert.True((Shown(session, CacheKind.Instruction), Shown(session, CacheKind.Data)) == seen[^1], $"{where}, cycle {session.Cycle}");
            }

            // Then any cycle in any order shows what was there then.
            var random = new SeededRandom(Seed + 0x100 + (ulong)index);
            for (var jump = 0; jump < 60; jump++)
            {
                var cycle = (ulong)random.Next(0, seen.Count - 1);
                session.Seek(cycle);
                Assert.True(
                    (Shown(session, CacheKind.Instruction), Shown(session, CacheKind.Data)) == seen[(int)cycle],
                    $"{where}, back at cycle {cycle}");
            }
        }
    }

    [Fact]
    public void TheCachesOnOfferAreShownOnTheSwitchesAndSoIsOneThatIsNot()
    {
        var names = SwitchBoard.Of(PipelineConfig.Default, Fetchline.Viz.Explain.EnglishMessages.Instance)
            .ToDictionary(group => group.Title, group => string.Join(' ', group.Options.Select(option => option.Chosen ? $"[{option.Name}]" : option.Name)));
        Assert.Equal("[off] 4x1x16:10 16x1x16:10", names["icache"]);
        Assert.Equal("[off] 4x1x16:10 2x2x16:10 16x1x16:10", names["dcache"]);

        // A link or a command line can ask for a cache that is not on offer: it is added to the
        // switch, so that the switch can show where it is and can be moved off it.
        var odd = new PipelineConfig { DataCache = new CacheConfig(8, 4, 32, 3) };
        var group = SwitchBoard.Of(odd, Fetchline.Viz.Explain.EnglishMessages.Instance).Single(group => group.Title == "dcache");
        Assert.Equal("off 4x1x16:10 2x2x16:10 16x1x16:10 [8x4x32:3]", string.Join(' ', group.Options.Select(option => option.Chosen ? $"[{option.Name}]" : option.Name)));
        Assert.Null(group.Options[0].Gives.DataCache);
        Assert.Equal(odd, group.Options[^1].Gives);

        // Every cache on offer can be built.
        Assert.All(SwitchBoard.InstructionCaches.Concat(SwitchBoard.DataCaches).OfType<CacheConfig>(), cache => cache.Validate());
    }
}
