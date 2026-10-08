using System.Globalization;
using System.Text.RegularExpressions;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Cli;

/// <summary><c>fetchline compare</c>: one program on the pipeline built every way.</summary>
public partial class CompareTests
{
    /// <summary>The rows of a table, cell by cell, and the lines under it.</summary>
    private static (List<string[]> Rows, List<string> Footer) Read(string table)
    {
        var lines = table.Split('\n');
        Assert.Equal(
            ["hazards", "branch", "predictor", "cycles", "CPI", "stalls", "squashed", "wrong guesses"],
            Columns().Split(lines[0].Trim()));

        // A row starts with the name of a way to handle hazards; what is left is the footer.
        var names = SwitchNames.All<HazardHandling>(SwitchNames.Of);
        var rows = lines.Skip(1)
            .Where(line => names.Any(name => line.StartsWith(" " + name + " ", StringComparison.Ordinal)))
            .Select(line => Columns().Split(line.Trim()))
            .ToList();
        var footer = lines.Skip(1)
            .Where(line => line.Length > 0 && !names.Any(name => line.StartsWith(" " + name + " ", StringComparison.Ordinal)))
            .ToList();
        return (rows, footer);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();

    [Fact]
    public void TheTableForTheSumIsWhatCanBeWorkedOutByHand()
    {
        var table = RunOk("compare", Example("sum.s"));
        var (rows, footer) = Read(table);

        Golden.Check("sum.compare.txt", table);
        Assert.Equal(3 * 2 * 4, rows.Count);

        // Forty instructions. The loop's branch is decided ten times and taken nine. The three
        // system calls cost the same whatever the switches: three slots behind each of the first
        // two (five instructions and the end of the code), none behind the exit.
        //
        // Decided in EX a wrong guess costs two cycles and squashes two instructions:
        //   not-taken       9 wrong   40 + 4 + 18 + 6 = 68   squashed 18 + 5
        //   backward-taken  1 wrong   40 + 4 +  2 + 6 = 52   squashed  2 + 5
        //   1-bit, 2-bit    2 wrong   40 + 4 +  4 + 6 = 54   squashed  4 + 5
        // Decided in ID it costs one, and every one of the ten waits a cycle for the addi ahead:
        //   not-taken       40 + 4 + 9 + 10 + 6 = 69         squashed  9 + 5
        //   backward-taken  40 + 4 + 1 + 10 + 6 = 61         squashed  1 + 5
        //   1-bit, 2-bit    40 + 4 + 2 + 10 + 6 = 62         squashed  2 + 5
        Assert.Equal(
            [
                "forwarding ex not-taken 68 1.70 0 23 9 of 10",
                "forwarding ex backward-taken 52 1.30 0 7 1 of 10",
                "forwarding ex 1-bit 54 1.35 0 9 2 of 10",
                "forwarding ex 2-bit 54 1.35 0 9 2 of 10",
                "forwarding id not-taken 69 1.73 10 14 9 of 10",
                "forwarding id backward-taken 61 1.53 10 6 1 of 10",
                "forwarding id 1-bit 62 1.55 10 7 2 of 10",
                "forwarding id 2-bit 62 1.55 10 7 2 of 10",
            ],
            rows.Take(8).Select(row => string.Join(' ', row)));

        // Stalling only: the first add waits one cycle for the li two ahead, and the branch
        // waits two for the addi just ahead of it every time round: 1 + 10 * 2 = 21 stalls,
        // whichever stage decides the branch, and the flushes are what they were.
        Assert.Equal("stall ex not-taken 89 2.23 21 23 9 of 10", string.Join(' ', rows[8]));
        Assert.Equal("stall id not-taken 80 2.00 21 14 9 of 10", string.Join(' ', rows[12]));
        Assert.All(rows.Skip(8).Take(8), row => Assert.Equal("21", row[5]));

        // With nothing handled the loop goes round an eleventh time and the answer is wrong.
        Assert.Equal("off ex not-taken 73 1.70 0 25 10 of 11 wrong", string.Join(' ', rows[16]));
        Assert.All(rows.Skip(16), row => Assert.Equal("wrong", row[^1]));
        Assert.All(rows.Take(16), row => Assert.Equal(8, row.Length));

        Assert.Equal(
            [
                " 40 instructions; fewest cycles with the right answer: forwarding, ex, backward-taken (52)",
                " off, ex, not-taken: first wrong value: instruction 4, 'add a0, a0, t0', in cycle 8: " +
                "the pipeline wrote a0 = 0x00000000, the reference machine wrote a0 = 0x00000001",
            ],
            footer);
    }

    [Fact]
    public void ASwitchThatIsGivenIsKeptFixedAndTheOthersAreTriedEveryWay()
    {
        var (two, _) = Read(RunOk("compare", Example("sum.s"), "--hazards", "stall", "--branch", "id"));
        Assert.Equal(
            ["stall id not-taken", "stall id backward-taken", "stall id 1-bit", "stall id 2-bit"],
            two.Select(row => string.Join(' ', row.Take(3))));

        var (one, _) = Read(RunOk("compare", Example("sum.s"), "--predictor", "2-bit"));
        Assert.Equal(
            ["forwarding ex 2-bit", "forwarding id 2-bit", "stall ex 2-bit", "stall id 2-bit", "off ex 2-bit", "off id 2-bit"],
            one.Select(row => string.Join(' ', row.Take(3))));

        // With all three given there is one row and nothing to rank, so nothing is said under it.
        var single = RunOk("compare", Example("sum.s"), "--hazards", "forwarding", "--branch", "ex", "--predictor", "not-taken");
        Assert.Equal(
            " hazards     branch  predictor  cycles   CPI  stalls  squashed  wrong guesses\n" +
            " forwarding  ex      not-taken      68  1.70       0        23        9 of 10\n",
            single);
    }

    [Fact]
    public void EveryRowCanBeTypedBackAsSwitchesAndGivesTheSameRun()
    {
        var (rows, _) = Read(RunOk("compare", Example("sum.s")));

        foreach (var row in rows)
        {
            var (exitCode, trace, _) = Run(
                "trace", Example("sum.s"), "--hazards", row[0], "--branch", row[1], "--predictor", row[2], "--cycles", "1", "--no-log");
            Assert.Equal(0, exitCode);

            var totals = Totals().Match(trace);
            Assert.True(totals.Success, trace);
            Assert.Equal((row[3], row[4]), (totals.Groups["cycles"].Value, totals.Groups["cpi"].Value));
        }
    }

    [GeneratedRegex(@"^ \d+ instructions?, (?<cycles>\d+) cycles?, CPI (?<cpi>[\d.]+),", RegexOptions.Multiline)]
    private static partial Regex Totals();

    [Fact]
    public void TheSwitchesWithNoShortListOfValuesAreTheSameInEveryRow()
    {
        var (quick, _) = Read(RunOk("compare", Example("multiply.s"), "--hazards", "forwarding"));
        var (slow, _) = Read(RunOk("compare", Example("multiply.s"), "--hazards", "forwarding", "--muldiv", "4"));

        // No branches in it, so nothing but the multiplier makes any difference.
        Assert.All(quick, row => Assert.Equal(("10", "0", "0 of 0"), (row[3], row[5], row[7])));
        Assert.All(slow, row => Assert.Equal(("16", "6", "0 of 0"), (row[3], row[5], row[7])));
    }

    [Fact]
    public void AProgramWithNoCloseDependenciesIsRightEvenWithNothingHandled()
    {
        using var source = Source("li a0, 1\nli a1, 2\nli a2, 3\n");

        var table = RunOk("compare", source.Path);
        var (rows, footer) = Read(table);

        // "off" is marked wrong only when it is. Here every way of building it takes seven cycles.
        Assert.All(rows, row => Assert.Equal(("7", "2.33", 8), (row[3], row[4], row.Length)));
        Assert.Equal([" 3 instructions; fewest cycles with the right answer: forwarding, ex, not-taken (7)"], footer);
        Assert.DoesNotContain("wrong value", table);
    }

    [Fact]
    public void AConfigurationThatNeverEndsIsCutOffAndSaysSo()
    {
        // Run correctly, the branch reads the zero just written and falls through. With nothing
        // handled it reads the one from four instructions earlier, every time, for ever.
        using var source = Source("loop:\nli t0, 1\nnop\nnop\nnop\nli t0, 0\nbnez t0, loop\n");

        var (exitCode, table, error) = Run("compare", source.Path, "--predictor", "not-taken", "--max-cycles", "200");
        var (rows, footer) = Read(table);

        Assert.Equal((0, string.Empty), (exitCode, error));
        Assert.Equal(
            [
                "forwarding ex not-taken 10 1.67 0 0 0 of 1",
                "forwarding id not-taken 11 1.83 1 0 0 of 1",
                "stall ex not-taken 12 2.00 2 0 0 of 1",
                "stall id not-taken 12 2.00 2 0 0 of 1",
            ],
            rows.Take(4).Select(row => string.Join(' ', row)));
        Assert.All(rows.Skip(4), row => Assert.Equal("wrong, still running after 200 cycles", row[^1]));
        Assert.All(rows.Skip(4), row => Assert.Equal("200", row[3]));
        Assert.StartsWith(" 6 instructions; fewest cycles with the right answer: forwarding, ex, not-taken (10)", footer[0]);
        Assert.StartsWith(" off, ex, not-taken: first wrong value: instruction 6, 'bnez t0, loop', in cycle 10: the pipeline went on to loop", footer[1]);
    }

    [Fact]
    public void AProgramThatNeverEndsAtAllIsAFailure()
    {
        using var source = Source("spin: j spin\n");

        var (exitCode, table, error) = Run("compare", source.Path, "--hazards", "forwarding", "--max-cycles", "50");
        var (rows, footer) = Read(table);

        Assert.Equal(1, exitCode);
        Assert.Equal("fetchline: still running after 50 cycles; --max-cycles raises the limit\n", error);
        Assert.All(rows, row => Assert.Equal("still running after 50 cycles", row[^1]));
        Assert.Empty(footer);
    }

    [Fact]
    public void AProgramThatFaultsIsComparedUpToTheFaultAndReported()
    {
        using var source = Source("li a0, 5\nnop\nnop\nnop\nsw a0, 0(zero)\nli a1, 1\n");

        var (exitCode, table, error) = Run("compare", source.Path, "--hazards", "forwarding", "--branch", "ex");
        var (rows, footer) = Read(table);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("fetchline: the program stopped: a store to 0x00000000", error);
        Assert.Equal(4, rows.Count);

        // A run that ends in a fault has no right answer to be quickest at.
        Assert.Empty(footer);
    }

    [Fact]
    public void AFileThatIsNotThereOrDoesNotAssembleIsRefused()
    {
        var (missing, _, missingError) = Run("compare", Path.Combine(Path.GetTempPath(), "fetchline-no-such-file.s"));
        Assert.Equal(2, missing);
        Assert.NotEmpty(missingError);

        using var source = Source("bogus a0\n");
        var (broken, output, brokenError) = Run("compare", source.Path);
        Assert.Equal((1, string.Empty), (broken, output));
        Assert.Contains("unknown instruction 'bogus'", brokenError);
    }

    [Fact]
    public void TheHelpSaysThatASwitchLeftOutIsTriedEveryWay()
    {
        // The help is wrapped to the width of the terminal, so it is read with its spacing evened out.
        static string Help(string command) => Spaces().Replace(RunOk(command, "--help"), " ");

        var compare = Help("compare");
        Assert.Contains("--hazards <forwarding|off|stall>", compare);
        Assert.Contains("or not at all. Every one is tried unless this is given.", compare);
        Assert.DoesNotContain("[default: forwarding]", compare);
        Assert.Contains("[default: 64]", compare);

        // A command that runs one configuration says which it is.
        var trace = Help("trace");
        Assert.Contains("[default: forwarding]", trace);
        Assert.DoesNotContain("Every one is tried", trace);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [Fact]
    public void TheComparisonCoversEveryCombinationInTheOrderOfTheTable()
    {
        var all = Comparison.Configurations();

        Assert.Equal(24, all.Count);
        Assert.Equal(24, all.Distinct().Count());
        Assert.Equal(PipelineConfig.Default, all[0]);
        Assert.Equal(
            new PipelineConfig { Hazards = HazardHandling.Off, Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit },
            all[^1]);
        Assert.Equal(16, all.Count(config => config.IsCorrect));

        // What is not varied is carried into every row.
        var basis = new PipelineConfig { BtbEntries = 16, MulDivCycles = 3 };
        var some = Comparison.Configurations(basis, hazards: [HazardHandling.StallOnly], predictors: [Predictor.OneBit, Predictor.TwoBit]);
        Assert.Equal(4, some.Count);
        Assert.All(some, config => Assert.Equal((HazardHandling.StallOnly, 16, 3), (config.Hazards, config.BtbEntries, config.MulDivCycles)));
    }

    [Fact]
    public void ARowIsTheSameRunAsOneMadeByHand()
    {
        var program = AssemblerTesting.Assemble(File.ReadAllText(Example("bubble-sort.s")));
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit };

        var row = Comparison.Run(program, config, maxCycles: 1_000_000);

        var lockstep = new Lockstep(program, new StringWriter(), config: config);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished)
        {
            records.Add(lockstep.Step());
        }

        var stats = PipelineStats.Of(records);
        Assert.True(row.IsRight && row.Ended);
        Assert.Equal(Core.Trace.StopReason.Exit, row.Stopped);
        Assert.Equal(
            (stats.Cycles, stats.Instructions, stats.Stalls, stats.Squashed, stats.Mispredictions, stats.Branches),
            (row.Stats.Cycles, row.Stats.Instructions, row.Stats.Stalls, row.Stats.Squashed, row.Stats.Mispredictions, row.Stats.Branches));
        Assert.Equal(row.Stats.Cpi.ToString("0.00", CultureInfo.InvariantCulture), stats.Cpi.ToString("0.00", CultureInfo.InvariantCulture));

        // Cut off early, the row says so and holds what had happened by then.
        var cut = Comparison.Run(program, config, maxCycles: 100);
        Assert.False(cut.Ended);
        Assert.Equal(100ul, cut.Stats.Cycles);
    }

    [Fact]
    public void TheNamesOfTheSwitchesGoBothWays()
    {
        Assert.Equal(["forwarding", "stall", "off"], SwitchNames.All<HazardHandling>(SwitchNames.Of));
        Assert.Equal(["ex", "id"], SwitchNames.All<BranchDecision>(SwitchNames.Of));
        Assert.Equal(["not-taken", "backward-taken", "1-bit", "2-bit"], SwitchNames.All<Predictor>(SwitchNames.Of));

        foreach (var value in Enum.GetValues<Predictor>())
        {
            Assert.Equal(value, SwitchNames.Parse<Predictor>(SwitchNames.Of(value), SwitchNames.Of));
        }

        Assert.Equal(HazardHandling.StallOnly, SwitchNames.Parse<HazardHandling>("stall", SwitchNames.Of));
        Assert.Equal(BranchDecision.Decode, SwitchNames.Parse<BranchDecision>("id", SwitchNames.Of));
        var error = Assert.Throws<ArgumentException>(() => SwitchNames.Parse<BranchDecision>("wb", SwitchNames.Of));
        Assert.Contains("ex, id", error.Message);
        Assert.Equal("stall, id, 1-bit", SwitchNames.Of(new PipelineConfig
        {
            Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, Predictor = Predictor.OneBit,
        }));
    }
}
