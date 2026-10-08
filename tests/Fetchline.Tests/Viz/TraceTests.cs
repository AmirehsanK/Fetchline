using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Pipeline;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;
using Fetchline.Viz.Staircase;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Viz;

/// <summary>The staircase diagram, the hazard log, and <c>fetchline trace</c>.</summary>
public class TraceTests
{
    // The textbook sequences. Their diagrams are what the textbooks draw, so they are pinned.
    [Theory]
    [InlineData("load-use.s")]
    [InlineData("forwarding.s")]
    [InlineData("branch.s")]
    [InlineData("hello.s")]
    public void TheTextbookSequencesGiveTheTextbookDiagrams(string example)
    {
        var trace = RunOk("trace", Example(example));

        Golden.Check(Path.ChangeExtension(example, ".trace.txt"), trace);
    }

    [Fact]
    public void TheLoadUseDiagramIsTheOneInTheSpecification()
    {
        // Spelled out here as well as in its golden file: this is the picture the project
        // promises, and it should take more than one careless update to change it.
        Assert.Equal(
            """
                                      1    2    3    4    5    6    7    8
             0x00 lw   x4, 0(x2)      IF   ID   EX   MEM  WB
             0x04 add  x5, x4, x6          IF   ID   ID   EX   MEM  WB
             0x08 sub  x7, x5, x4               IF   IF   ID   EX   MEM  WB

             c3  stall    load-use: add (ID) needs x4; lw (EX) has it only after MEM
             c5  forward  MEM/WB -> EX.A   x4 from lw
             c6  forward  EX/MEM -> EX.A   x5 from add
             3 instructions, 8 cycles, CPI 2.67, 1 stall, 2 forwards

            """.ReplaceLineEndings("\n"),
            RunOk("trace", Example("load-use.s")));
    }

    [Fact]
    public void ALongRunIsShownThroughAWindow()
    {
        Golden.Check("sum-first-22.trace.txt", RunOk("trace", Example("sum.s"), "--cycles", "22"));
        Golden.Check("sum-from-50.trace.txt", RunOk("trace", Example("sum.s"), "--from", "50", "--cycles", "0"));

        var whole = RunOk("trace", Example("sum.s"), "--cycles", "0", "--no-log");
        Assert.DoesNotContain("--from and --cycles", whole);
        Assert.DoesNotContain("forward", whole.Replace("forwards", string.Empty));
        Assert.EndsWith(" 40 instructions, 68 cycles, CPI 1.70, 0 stalls, 11 forwards, 11 flushes\n", whole);
    }

    [Fact]
    public void TheTotalsCoverTheWholeRunWhateverTheWindowShows()
    {
        var first = RunOk("trace", Example("sum.s"), "--cycles", "5");
        var last = RunOk("trace", Example("sum.s"), "--from", "60");

        Assert.Equal(first.Split('\n')[^2], last.Split('\n')[^2]);
        Assert.Contains(" cycles 1 to 5 of 68; --from and --cycles show the rest\n", first);
        Assert.Contains(" cycles 60 to 68 of 68; --from and --cycles show the rest\n", last);
    }

    [Fact]
    public void AFaultIsDrawnUpToWhereItHappenedAndReported()
    {
        using var source = Source("li a0, 5\nsw a0, 0(zero)\nli a1, 1\nli a2, 2\n");

        var (exitCode, output, error) = Run("trace", source.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains(" 0x04 sw   a0, 0(zero)          IF   ID   EX   MEM  WB\n", output);
        Assert.Contains(" 0x08 li   a1, 1                     IF   ID   EX   --\n", output);
        Assert.Contains("sw (MEM) stops the machine; 2 instructions behind it are squashed", output);
        Assert.StartsWith("fetchline: the program stopped: a store to 0x00000000", error);
    }

    [Fact]
    public void AProgramThatNeverEndsIsDrawnUpToTheLimit()
    {
        using var source = Source("spin: j spin\n");

        var (exitCode, output, error) = Run("trace", source.Path, "--max-cycles", "30", "--cycles", "9");

        Assert.Equal(1, exitCode);
        // The first jump leaves WB in cycle 5, and one more every three cycles after it.
        Assert.Contains(" 9 instructions, 30 cycles, CPI 3.33", output);
        Assert.Equal("fetchline: still running after 30 cycles; --max-cycles raises the limit\n", error);
    }

    [Fact]
    public void TheStaircaseHasARowForEveryInstructionFetchedAndACellForEveryCycleItWasThere()
    {
        var run = PipelineTesting.RunToEnd(File.ReadAllText(Repo.PathOf("examples", "branch.s")));
        var layout = StaircaseLayout.Build(run.Records);

        Assert.Equal(10ul, layout.Cycles);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul], layout.Rows.Select(row => row.Seq));
        Assert.Equal([0u, 4u, 8u, 12u, 16u, 20u], layout.Rows.Select(row => row.Pc));
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul], layout.Rows.Select(row => row.FirstCycle));

        var squashedInDecode = layout.Row(4)!;
        Assert.Equal(
            [new StaircaseCell(Stage.Fetch, Occupancy.Normal), new StaircaseCell(Stage.Decode, Occupancy.Squashed)],
            squashedInDecode.Cells);
        Assert.True(squashedInDecode.WasSquashed);
        Assert.False(squashedInDecode.Completed);
        Assert.Equal(5ul, squashedInDecode.LastCycle);
        Assert.Null(squashedInDecode.At(3));
        Assert.Equal(Stage.Decode, squashedInDecode.At(5)!.Value.Stage);
        Assert.Null(squashedInDecode.At(6));

        var completed = layout.Row(6)!;
        Assert.True(completed.Completed);
        Assert.False(completed.WasSquashed);
        Assert.Equal(5, completed.Cells.Count);
        Assert.Null(layout.Row(7));
        Assert.Null(layout.Row(0));
    }

    [Fact]
    public void AHeldInstructionHasTheSameStageInTwoCells()
    {
        var run = PipelineTesting.RunToEnd(File.ReadAllText(Repo.PathOf("examples", "load-use.s")));
        var layout = StaircaseLayout.Build(run.Records);

        Assert.Equal(
            [
                new StaircaseCell(Stage.Fetch, Occupancy.Normal),
                new StaircaseCell(Stage.Decode, Occupancy.Held),
                new StaircaseCell(Stage.Decode, Occupancy.Normal),
                new StaircaseCell(Stage.Execute, Occupancy.Normal),
                new StaircaseCell(Stage.Memory, Occupancy.Normal),
                new StaircaseCell(Stage.WriteBack, Occupancy.Normal),
            ],
            layout.Row(2)!.Cells);
        Assert.Equal(Occupancy.Held, layout.Row(3)!.Cells[0].State);
    }

    [Fact]
    public void AnInstructionIsShownAsItWasWrittenWhenItCameFromOneLine()
    {
        var program = AssemblerTesting.Assemble("""
            main:
                LI    a0,5
                bnez  a0 ,  done
                la    t0, main
            done:
                sw    a0,  8 ( sp )
            """);
        var labels = new InstructionLabels(program);

        string At(uint pc)
        {
            Assert.True(program.TryReadWord(pc, out var word));
            return labels.Describe(pc, word).ToString();
        }

        Assert.Equal("li a0, 5", At(0));                   // spacing made regular, mnemonic lower case
        Assert.Equal("bnez a0, done", At(4));              // the label, not its address
        Assert.Equal("auipc t0, 0x0", At(8));              // la became two instructions: each is itself
        Assert.Equal("addi t0, t0, -8", At(12));
        Assert.Equal("sw a0, 8 ( sp )", At(16));
        Assert.Equal("done", labels.Address(16));
        Assert.Equal("0x40", labels.Address(0x40));
    }

    [Fact]
    public void RegistersAreNamedTheWayTheProgramNamesThem()
    {
        var numeric = new InstructionLabels(AssemblerTesting.Assemble("add x5, x6, x7\nla x8, there\nthere:"));
        var abi = new InstructionLabels(AssemblerTesting.Assemble("add t0, t1, t2\nla s0, there\nthere:"));
        var mixed = new InstructionLabels(AssemblerTesting.Assemble("add t0, t1, x7"));

        Assert.Equal((RegisterStyle.Numeric, "x4"), (numeric.Registers, numeric.Register(4)));
        Assert.Equal((RegisterStyle.Abi, "tp"), (abi.Registers, abi.Register(4)));
        Assert.Equal(RegisterStyle.Abi, mixed.Registers);

        // The halves of a pseudo-instruction are disassembled, in the program's own style.
        Assert.Equal("auipc x8, 0x0", numeric.Describe(4, 0x00000417).ToString());
        Assert.Equal("auipc s0, 0x0", abi.Describe(4, 0x00000417).ToString());
    }

    [Fact]
    public void TheLogNamesTheInstructionsEachLineIsAbout()
    {
        var program = AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "load-use.s")));
        var machine = new PipelineMachine(program);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished)
        {
            records.Add(machine.Step());
        }

        var explainer = new Explainer(StaircaseLayout.Build(records), new InstructionLabels(program), EnglishMessages.Instance);
        var lines = explainer.Explain(records);

        Assert.Equal(
            [
                new LogLine(3, LogKind.Stall, "load-use: add (ID) needs x4; lw (EX) has it only after MEM", Seq: 2, Other: 1),
                new LogLine(5, LogKind.Forward, "MEM/WB -> EX.A   x4 from lw", Seq: 2, Other: 1),
                new LogLine(6, LogKind.Forward, "EX/MEM -> EX.A   x5 from add", Seq: 3, Other: 2),
            ],
            lines);
        Assert.Equal(["stall", "forward", "flush", "trap"], Enum.GetValues<LogKind>().Select(explainer.Label));
    }

    [Fact]
    public void TheTotalsAreWrittenInGoodEnglishForOneAndForMany()
    {
        var messages = EnglishMessages.Instance;

        Assert.Equal("1 instruction, 5 cycles, CPI 5.00, 0 stalls, 0 forwards", messages.Summary(1, 5, 5, 0, 0, 0));
        Assert.Equal("2 instructions, 1 cycle, CPI 0.50, 1 stall, 1 forward, 1 flush", messages.Summary(2, 1, 0.5, 1, 1, 1));
        Assert.Equal("3 instructions, 9 cycles, CPI 3.00, 2 stalls, 3 forwards, 2 flushes", messages.Summary(3, 9, 3, 2, 3, 2));
        Assert.Equal("j (EX) is taken to loop; 1 instruction behind it is squashed", messages.TakenBranch("j", "loop", 1));
        Assert.Equal("ecall (MEM) stops the machine; 0 instructions behind it are squashed", messages.Stopped("ecall", 0));
    }
}
