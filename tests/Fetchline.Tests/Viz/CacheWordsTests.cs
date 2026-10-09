using Fetchline.Core.Pipeline;
using Fetchline.Tests.Cli;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;
using Fetchline.Viz.State;

namespace Fetchline.Tests.Viz;

/// <summary>How a cache is typed, and what is said and counted about one.</summary>
public class CacheWordsTests
{
    [Fact]
    public void ACacheIsTypedAsItsShapeAndItsPenalty()
    {
        Assert.Equal("off", SwitchNames.Of((CacheConfig?)null));
        Assert.Equal("16x2x32:7", SwitchNames.Of(new CacheConfig(16, 2, 32, 7)));

        Assert.True(SwitchNames.TryParseCache("off", out var none, out _));
        Assert.Null(none);
        Assert.True(SwitchNames.TryParseCache("16x2x32:7", out var cache, out _));
        Assert.Equal(new CacheConfig(16, 2, 32, 7), cache);

        // The penalty may be left out, and is then ten cycles.
        Assert.True(SwitchNames.TryParseCache("8x1x16", out cache, out _));
        Assert.Equal(new CacheConfig(8, 1, 16, 10), cache);

        // Whatever is written out can be read back.
        foreach (var shape in new[] { new CacheConfig(1, 1, 4, 1), new CacheConfig(1024, 8, 256, 64), new CacheConfig(4, 3, 8, 12) })
        {
            Assert.True(SwitchNames.TryParseCache(SwitchNames.Of(shape), out var back, out _));
            Assert.Equal(shape, back);
        }
    }

    [Theory]
    [InlineData("", "is not a cache")]
    [InlineData("on", "is not a cache")]
    [InlineData("16", "is not a cache")]
    [InlineData("16x2", "is not a cache")]
    [InlineData("16x2x32x4", "is not a cache")]
    [InlineData("16x2x32:", "is not a cache")]
    [InlineData("16x2x32:7:1", "is not a cache")]
    [InlineData("16x-2x32", "is not a cache")]
    [InlineData("16x 2x32", "is not a cache")]
    [InlineData("+16x2x32", "is not a cache")]
    [InlineData("16x2x32:1e1", "is not a cache")]
    [InlineData("999999x2x32", "is not a cache")]
    [InlineData("3x2x32", "power-of-two number of sets")]
    [InlineData("4x9x32", "ways")]
    [InlineData("4x2x2", "bytes")]
    [InlineData("4x2x32:0", "cycles")]
    [InlineData("4x2x32:65", "cycles")]
    public void WhatIsNotACacheIsRefusedWithTheReason(string text, string reason)
    {
        Assert.False(SwitchNames.TryParseCache(text, out var cache, out var problem));
        Assert.Null(cache);
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void AMissIsSaidOnceWithHowLongItTakesAndWhatItPutsOut()
    {
        // One set of one way: the last load puts out the block the first two were in.
        var config = new PipelineConfig { DataCache = new CacheConfig(1, 1, 8, 3) };
        var session = new Session(".data\nfirst: .word 1, 2\nsecond: .word 3\n.text\nla t0, first\nlw t1, 0(t0)\nlw t2, 4(t0)\nlw t3, 8(t0)\n", config);
        session.Seek(ulong.MaxValue);
        var labels = new InstructionLabels(session.Program!);
        var records = session.Recorded(1, session.Frontier);

        var english = HazardLog.Build(records, labels, EnglishMessages.Instance, null).Where(line => line.Kind == LogKind.Stall).ToList();
        Assert.Equal(
            [
                "data cache miss: lw (MEM) waits 3 cycles for the block at first; what is behind it waits too",
                "data cache miss: lw (MEM) waits 3 cycles for the block at second; what is behind it waits too; the block at first is put out",
            ],
            english.Select(line => line.Text));

        // Three cycles of waiting, one line: the line is in the cycle the miss was found.
        Assert.Equal(6, session.Stats.StallsBy(StallCause.DataCacheMiss));

        var persian = HazardLog.Build(records, labels, PersianMessages.Instance, null).Where(line => line.Kind == LogKind.Stall).ToList();
        Assert.Equal(
            "فقدان در حافظه‌ی نهان داده: lw در MEM 3 چرخه منتظر بلوک second می‌ماند؛ هرچه پشت آن است نیز منتظر می‌ماند؛ بلوک first بیرون گذاشته می‌شود",
            persian[1].Text);

        // A miss in IF holds nothing else, and says so by not saying otherwise.
        var fetch = new Session("nop\nnop\n", new PipelineConfig { InstructionCache = new CacheConfig(2, 1, 4, 1) });
        fetch.Seek(ulong.MaxValue);
        var lines = HazardLog.Build(fetch.Recorded(1, fetch.Frontier), new InstructionLabels(fetch.Program!), EnglishMessages.Instance, null);
        Assert.Equal("instruction cache miss: nop (IF) waits 1 cycle for the block at 0x0", lines[0].Text);
    }

    [Fact]
    public void TheCountersAndTheTraceSayHowOftenEachCacheMissed()
    {
        const string Source = "lw t0, 0(sp)\nlw t1, 4(sp)\nlw t2, 0(sp)\n";
        var config = new PipelineConfig { DataCache = new CacheConfig(2, 1, 4, 2) };
        var session = new Session(Source, config);
        session.Seek(ulong.MaxValue);

        // The word at sp and the word after it are two blocks in two sets; the third load hits.
        var counters = Counters.Of(session.Stats, EnglishMessages.Instance);
        Assert.Contains(new Counter("data cache misses", "2/3"), counters);
        Assert.Contains(new Counter("data cache miss", "4", Part: true), counters);
        Assert.DoesNotContain(counters, counter => counter.Label.StartsWith("instruction cache", StringComparison.Ordinal));

        // With no cache there is no line about one.
        var plain = new Session(Source);
        plain.Seek(ulong.MaxValue);
        Assert.DoesNotContain(Counters.Of(plain.Stats, EnglishMessages.Instance), counter => counter.Label.Contains("cache", StringComparison.Ordinal));

        var trace = AsciiTrace.Write(session.Program!, session.Recorded(1, session.Frontier), EnglishMessages.Instance);
        Assert.Contains(" data cache: 3 accesses, 2 misses (66.7%)\n", trace);
        Assert.DoesNotContain("instruction cache:", trace);
        Assert.DoesNotContain("cache", AsciiTrace.Write(plain.Program!, plain.Recorded(1, plain.Frontier), EnglishMessages.Instance));
    }

    [Fact]
    public void TheCommandLineBuildsThePipelineWithTheCachesItIsGiven()
    {
        var source = Repo.PathOf("examples", "bubble-sort.s");

        var trace = CommandLine.RunOk("trace", source, "--icache", "4x1x16:3", "--dcache", "2x1x8:4", "--cycles", "20");
        Assert.Contains("instruction cache miss: auipc (IF) waits 3 cycles for the block at main", trace);
        Assert.Contains("data cache miss: lw (MEM) waits 4 cycles for the block at array; what is behind it waits too", trace);
        Assert.Matches(@" instruction cache: \d+ accesses, \d+ misses \(\d+\.\d%\)\n data cache: \d+ accesses, \d+ misses", trace);

        // Without the switches nothing about a cache is printed at all.
        Assert.DoesNotContain("cache", CommandLine.RunOk("trace", source));

        // A comparison keeps the caches in every row and says so under the table.
        var table = CommandLine.RunOk("compare", source, "--hazards", "forwarding", "--branch", "ex", "--dcache", "2x1x8:4");
        Assert.Contains(" each with data cache 2x1x8:4\n", table);
        Assert.DoesNotContain("each with", CommandLine.RunOk("compare", source, "--hazards", "forwarding", "--branch", "ex"));

        var json = CommandLine.RunOk("trace", source, "--format", "json", "--dcache", "2x1x8:4");
        Assert.Contains("\"dcache\":{\"sets\":2,\"ways\":1,\"blockBytes\":8,\"missPenalty\":4}", json);
        Assert.Contains("\"icache\":null", json);
        Assert.Contains("\"type\":\"cache\"", json);
        Assert.Contains("\"cause\":\"data-cache-miss\"", json);

        foreach (var bad in new[] { "banana", "3x1x8", "4x1x8:0" })
        {
            var (exitCode, _, error) = CommandLine.Run("trace", source, "--dcache", bad);
            Assert.NotEqual(0, exitCode);
            Assert.NotEqual(string.Empty, error);
        }

        // The official tests take the same switches, and only with the pipeline.
        var (refused, _, why) = CommandLine.Run("test", Repo.PathOf("tests", "vectors", "riscv-tests", "elf"), "--dcache", "2x1x8:4");
        Assert.NotEqual(0, refused);
        Assert.Contains("--pipeline", why);
    }
}
