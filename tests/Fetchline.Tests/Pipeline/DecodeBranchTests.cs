using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// Branches decided in ID, as Patterson and Hennessy draw the pipeline. A taken branch costs one
/// squashed instruction instead of two, and the price is that its operands are needed a stage
/// earlier: a result still being computed has to be waited for.
/// </summary>
public class DecodeBranchTests
{
    private static readonly PipelineConfig InDecode = new() { Branches = BranchDecision.Decode };

    private static PipelineRun Go(string source, PipelineConfig? config = null)
    {
        var lockstep = new Lockstep(AssemblerTesting.Assemble(source), new StringWriter(), config: config ?? InDecode);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < 100_000)
        {
            records.Add(lockstep.Step());
        }

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        return new PipelineRun(lockstep.Pipeline, records, lockstep.Pipeline.Hart.Output.ToString()!);
    }

    private static List<T> Events<T>(PipelineRun run)
        where T : PipelineEvent => [.. run.Records.SelectMany(record => record.Events.OfType<T>())];

    [Fact]
    public void ATakenBranchThrowsAwayOnlyTheOneInstructionBehindIt()
    {
        var run = Go("""
                nop
                nop
                beq  x0, x0, target      # 3: decided in ID
                li   a0, 1               # 4: in IF when it is decided
                li   a1, 2               #    never fetched
            target:
                li   a2, 3               # 5
            """);

        Assert.Equal("IF ID EX MEM WB", run.Row(3));
        Assert.Equal("IF xx", run.Row(4));
        Assert.Equal("IF ID EX MEM WB", run.Row(5));
        Assert.Equal(run.CycleOf(3, Stage.Decode) + 1, run.CycleOf(5, Stage.Fetch));

        Assert.Equal((0u, 0u, 3u), (run["a0"], run["a1"], run["a2"]));
        Assert.Equal(4 + 4 + 1, run.Cycles);
        Assert.Equal(
            [new BranchEvent(3, Taken: true, Target: 0x14, Mispredicted: true, Stage.Decode)],
            Events<BranchEvent>(run));
        Assert.Equal([new FlushEvent(4, FlushCause.Branch, Stage.Fetch, By: 3)], Events<FlushEvent>(run));
    }

    [Fact]
    public void AJumpCostsOneCycleAndItsLinkStillReachesTheNextInstruction()
    {
        var run = Go("jal ra, callee\nli a5, 9\ncallee:\naddi a0, ra, 0\naddi a1, ra, 100");

        Assert.Equal((4u, 104u, 0u), (run["a0"], run["a1"], run["a5"]));
        Assert.Equal(3 + 4 + 1, run.Cycles);
    }

    [Theory]
    // What the branch needs, where its producer is when the branch reaches ID, stalls, forwards.
    [InlineData("addi t0, zero, 1\nbnez t0, over", 1, 1)]                    // in EX: wait one cycle, then forward
    [InlineData("addi t0, zero, 1\nnop\nbnez t0, over", 0, 1)]               // in MEM: forward at once
    [InlineData("addi t0, zero, 1\nnop\nnop\nbnez t0, over", 0, 0)]          // in WB: the register file has it
    [InlineData("lw t0, 0(s0)\nbnez t0, over", 2, 0)]                        // a load in EX: wait twice
    [InlineData("lw t0, 0(s0)\nnop\nbnez t0, over", 1, 0)]                   // a load in MEM: wait once
    [InlineData("lw t0, 0(s0)\nnop\nnop\nbnez t0, over", 0, 0)]
    [InlineData("addi t0, zero, 1\naddi t1, zero, 1\nbeq t0, t1, over", 1, 1)]   // one in EX, one in MEM: after the wait, MEM and WB
    [InlineData("addi t0, zero, 1\naddi t1, zero, 1\nnop\nbeq t0, t1, over", 0, 1)]   // one in MEM, one in WB
    [InlineData("addi t1, zero, 5\nbnez t0, over", 0, 0)]                    // the instruction ahead writes something else
    [InlineData("la t0, over\njalr zero, 0(t0)", 1, 1)]                      // an indirect jump needs its register too
    public void TheComparatorWaitsOnlyForWhatIsNotComputedYet(string sequence, int stalls, int forwards)
    {
        var run = Go($"""
            .data
            one: .word 1
            .text
                lui  s0, 0x10000
                li   t0, 1
                nop
                nop
                nop
                {sequence.Replace("\n", "\n    ")}
                li   a0, 9
            over:
                li   a1, 7
            """);

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal(stalls, stats.StallsBy(StallCause.BranchOperand));
        Assert.Equal(stalls, stats.Stalls);
        Assert.Equal(forwards, Events<ForwardEvent>(run).Count(forward => forward.To == Stage.Decode));
        Assert.Equal((0u, 7u), (run["a0"], run["a1"]));
        Assert.All(Events<ForwardEvent>(run).Where(f => f.To == Stage.Decode), f => Assert.Equal(ForwardSource.ExMem, f.From));
    }

    [Fact]
    public void ABranchDecidedInDecodeHasNothingForwardedToItInExecute()
    {
        var run = Go("li t0, 0\nbnez t0, over\nli a0, 1\nover:\nli a1, 2");

        // The branch (2) waited a cycle for t0 and got it in ID. In EX it reads nothing.
        var forwards = Events<ForwardEvent>(run);
        Assert.Equal([new ForwardEvent(2, ForwardSource.ExMem, Operand.A, 5, 0, Producer: 1, Stage.Decode)], forwards);
        Assert.Equal("IF ID ID EX MEM WB", run.Row(2));
    }

    [Fact]
    public void ALoopPaysOneCycleToWaitAndOneWhenTaken()
    {
        const string loop = "li t0, 3\nloop:\naddi a0, a0, 10\naddi t0, t0, -1\nbnez t0, loop";
        var inDecode = Go(loop);
        var inExecute = Go(loop, PipelineConfig.Default);

        // Ten instructions either way. Decided in EX: two taken branches at two cycles each.
        // Decided in ID: every branch waits a cycle for the addi just ahead of it (three), and
        // the two taken ones cost one more each. The earlier decision is the slower one here.
        Assert.Equal(30u, inDecode["a0"]);
        Assert.Equal(10 + 4 + (2 * 2), inExecute.Cycles);
        Assert.Equal(10 + 4 + 3 + (2 * 1), inDecode.Cycles);
    }

    [Fact]
    public void WithStallingOnlyTheBranchWaitsLikeEverythingElse()
    {
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Hazards = HazardHandling.StallOnly };
        var run = Go("li t0, 1\nbnez t0, over\nli a0, 9\nover:\nli a1, 7", config);

        Assert.Equal("IF ID ID ID EX MEM WB", run.Row(2));
        Assert.Empty(Events<ForwardEvent>(run));
        Assert.All(Events<StallEvent>(run), stall => Assert.Equal(StallCause.DataHazard, stall.Cause));
        Assert.Equal((0u, 7u), (run["a0"], run["a1"]));
    }

    [Fact]
    public void WithHazardsOffTheComparatorReadsAStaleRegister()
    {
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Hazards = HazardHandling.Off };
        var lockstep = new Lockstep(
            AssemblerTesting.Assemble("li t0, 1\nbnez t0, over\nli a0, 9\nover:\nli a1, 7"), config: config);

        // Run stops at the first difference; here the pipeline is stepped on to its own end.
        while (!lockstep.Pipeline.IsFinished)
        {
            lockstep.Step();
        }

        // t0 still read as zero in ID, so the branch was not taken and the 9 was written.
        Assert.Equal(9u, lockstep.Pipeline.Hart.X[10]);
        Assert.Equal(
            "instruction 2, in cycle 6, 'bnez t0, 0xc' at 0x00000004: " +
            "the pipeline went on to 0x00000008, the reference machine to 0x0000000c",
            lockstep.Divergence!.Describe());
    }

    [Theory]
    [InlineData(HazardHandling.Forwarding)]
    [InlineData(HazardHandling.StallOnly)]
    public void EveryExampleRunsInLockstep(HazardHandling hazards)
    {
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Hazards = hazards };
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var lockstep = new Lockstep(AssemblerTesting.Assemble(File.ReadAllText(path)), new StringWriter(), config: config);
            lockstep.Pipeline.Recording = false;
            lockstep.Run(5_000_000);

            Assert.True(lockstep.Divergence is null, $"{Path.GetFileName(path)}: {lockstep.Divergence?.Describe()}");
            Assert.True(lockstep.Pipeline.IsFinished, Path.GetFileName(path));
            Assert.Equal(Cli.RunTests.ExpectedOutput(Path.GetFileName(path)), lockstep.Pipeline.Hart.Output.ToString());
        }
    }

    [Theory]
    [InlineData(HazardHandling.Forwarding)]
    [InlineData(HazardHandling.StallOnly)]
    public void RandomProgramsWithBranchesRunInLockstep(HazardHandling hazards)
    {
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Hazards = hazards };
        string[] registers = ["a0", "a1", "a2", "t0", "t1", "ra", "zero"];
        string[] conditions = ["beq", "bne", "blt", "bge", "bltu", "bgeu"];
        var random = new SeededRandom(0xF37C_6301);

        for (var round = 0; round < 400; round++)
        {
            var lines = new List<string> { ".data", "cells: .word 3, -1, 0, 7", ".text", "lui s0, 0x10000" };
            const int count = 50;
            for (var i = 0; i < count; i++)
            {
                lines.Add($"L{i}:");
                var skipTo = $"L{Math.Min(count, i + 1 + random.Next(0, 3))}";
                lines.Add(random.Next(0, 10) switch
                {
                    < 3 => $"{random.Pick(conditions)} {random.Pick(registers)}, {random.Pick(registers)}, {skipTo}",
                    3 => $"jal {random.Pick(registers)}, {skipTo}",
                    4 => $"lw {random.Pick(registers)}, {4 * random.Next(0, 3)}(s0)",
                    5 => $"sw {random.Pick(registers)}, {4 * random.Next(0, 3)}(s0)",
                    6 => $"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(-4, 4)}",
                    _ => $"sub {random.Pick(registers)}, {random.Pick(registers)}, {random.Pick(registers)}",
                });
            }

            lines.Add($"L{count}:");
            var source = string.Join('\n', lines);
            var lockstep = new Lockstep(AssemblerTesting.Assemble(source), config: config);
            lockstep.Pipeline.Recording = false;
            lockstep.Run(100_000);

            if (lockstep.Divergence is not null || !lockstep.Pipeline.IsFinished)
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: {lockstep.Divergence?.Describe() ?? "did not finish"}\n{source}");
            }
        }
    }

    [Fact]
    public void TheTraceSaysWhereTheBranchWasDecided()
    {
        var trace = RunOk("trace", Example("branch.s"), "--branch", "id");

        Golden.Check("branch-id.trace.txt", trace);
        Assert.Contains(" c3  stall    branch in ID: beq (ID) needs x5; li (EX) has it only after EX\n", trace);
        Assert.Contains(" c4  forward  EX/MEM -> ID.A   x5 from li\n", trace);
        Assert.Contains(" c5  flush    bne (ID) is taken to skip; 1 instruction behind it is squashed\n", trace);
    }

    [Fact]
    public void AWaitForALoadSaysItIsReadyOnlyAfterMem()
    {
        using var source = Source(".data\nv: .word 1\n.text\nlui s0, 0x10000\nnop\nnop\nnop\nlw t0, 0(s0)\nbnez t0, over\nnop\nover:\nnop\n");

        var trace = RunOk("trace", source.Path, "--branch", "id");

        Assert.Contains("branch in ID: bnez (ID) needs t0; lw (EX) has it only after MEM\n", trace);
        Assert.Contains("branch in ID: bnez (ID) needs t0; lw (MEM) has it only after MEM\n", trace);
    }
}
