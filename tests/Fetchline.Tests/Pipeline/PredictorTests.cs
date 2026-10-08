using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// The four ways fetch can guess where a branch goes. A guess is wrong only when fetch went
/// somewhere other than where the instruction really leads; a right guess makes a taken branch
/// free.
/// </summary>
public class PredictorTests
{
    private static readonly Predictor[] Predictors = Enum.GetValues<Predictor>();

    private static PipelineRun Go(string source, PipelineConfig config)
    {
        var lockstep = new Lockstep(AssemblerTesting.Assemble(source), new StringWriter(), config: config);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < 200_000)
        {
            records.Add(lockstep.Step());
        }

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        return new PipelineRun(lockstep.Pipeline, records, lockstep.Pipeline.Hart.Output.ToString()!);
    }

    private static PipelineConfig With(Predictor predictor, BranchDecision branches = BranchDecision.Execute, int btb = 256) =>
        new() { Predictor = predictor, Branches = branches, BtbEntries = btb };

    // A loop whose body is long enough that the predictor has always heard how the branch came
    // out before the branch is fetched again.
    private const string Loop = """
            li   t0, 5
        loop:
            addi a0, a0, 1
            addi a1, a1, 2
            addi a2, a2, 3
            addi t0, t0, -1
            bnez t0, loop
            li   a3, 9
        """;

    [Fact]
    public void ATakenBranchToTheVeryNextInstructionCostsNothing()
    {
        // Fetch was going there anyway, so nothing fetched behind the branch is wrong.
        var run = Go("beq x0, x0, next\nnext:\nli a0, 1\njal ra, after\nafter:\nli a1, 2", PipelineConfig.Default);

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((2, 2, 0, 0), (stats.Branches, stats.BranchesTaken, stats.Mispredictions, stats.Flushes));
        Assert.Equal(4 + 4, run.Cycles);
        Assert.Equal(12u, run["ra"]);
    }

    [Theory]
    [InlineData(Predictor.NotTaken, 4, 27 + 4 + (4 * 2))]          // every time round is a wrong guess
    [InlineData(Predictor.BackwardTaken, 1, 27 + 4 + (1 * 2))]     // only the last time, when it falls out
    [InlineData(Predictor.OneBit, 2, 27 + 4 + (2 * 2))]            // the first time, unknown; and the last
    [InlineData(Predictor.TwoBit, 2, 27 + 4 + (2 * 2))]
    public void ALoopCostsOnlyItsWrongGuesses(Predictor predictor, int mispredictions, int cycles)
    {
        var run = Go(Loop, With(predictor));
        var stats = PipelineStats.Of(run.Records);

        // 1 + 5 * 5 + 1 instructions. The branch is taken four times and falls through once.
        Assert.Equal((5, 4), (stats.Branches, stats.BranchesTaken));
        Assert.Equal(mispredictions, stats.Mispredictions);
        Assert.Equal(mispredictions, stats.FlushesBy(FlushCause.Branch));
        Assert.Equal(cycles, run.Cycles);
        Assert.Equal((5u, 9u), (run["a0"], run["a3"]));
    }

    [Fact]
    public void ARightGuessLeavesNoGapInTheDiagram()
    {
        var run = Go(Loop, With(Predictor.BackwardTaken));

        // The branch is instruction 6 (li, four addis, bnez). The instruction fetched in the
        // very next cycle is the top of the loop, and nothing is squashed.
        Assert.Equal("IF ID EX MEM WB", run.Row(6));
        Assert.Equal(run.CycleOf(6, Stage.Fetch) + 1, run.CycleOf(7, Stage.Fetch));
        Assert.Equal(4u, run.Records.First(r => r.Fetch.Seq == 7).Fetch.Pc);
        Assert.Equal("IF ID EX MEM WB", run.Row(7));
    }

    [Fact]
    public void AWrongGuessThatABranchIsTakenIsPutRightTheOtherWay()
    {
        // Backward-taken guesses the loop's branch is taken the last time too. Fetch goes back to
        // the top of the loop, and those instructions are thrown away when the branch falls through.
        var run = Go(Loop, With(Predictor.BackwardTaken));

        var wrong = run.Records.SelectMany(r => r.Events.OfType<BranchEvent>()).Single(b => b.Mispredicted);
        Assert.False(wrong.Taken);

        var flushed = run.Records.SelectMany(r => r.Events.OfType<FlushEvent>()).Where(f => f.By == wrong.Seq).ToList();
        Assert.Equal(2, flushed.Count);
        Assert.Equal(9u, run["a3"]);
        Assert.Equal(5u, run["a0"]);      // the loop body did not run a sixth time
    }

    [Fact]
    public void TheStaticRuleFollowsBackwardBranchesAndJalAndNothingElse()
    {
        var run = Go("""
                li   t0, 1
                nop
                nop
                beqz zero, forward       # forward and taken: guessed not taken, wrong
                nop
            forward:
                jal  ra, target          # a jal goes where it says: right
                nop
            target:
                la   t1, last
                nop
                nop
                jalr zero, 0(t1)         # an indirect jump cannot be read off the instruction: wrong
                nop
            last:
                bnez zero, forward       # backward and not taken: guessed taken, wrong
            """, With(Predictor.BackwardTaken));

        var branches = run.Records.SelectMany(r => r.Events.OfType<BranchEvent>()).ToList();
        Assert.Equal([true, false, true, true], branches.Select(b => b.Mispredicted));
        Assert.Equal([true, true, true, false], branches.Select(b => b.Taken));
    }

    [Fact]
    public void TwoBitsForgiveTheOneOddOutcomeAtTheEndOfAnInnerLoop()
    {
        // The inner loop runs four times round, three times over. One bit is wrong twice each
        // time the inner loop is entered again: once leaving it, once coming back. Two bits are
        // wrong only on leaving.
        const string nested = """
                li   s0, 3
            outer:
                li   t0, 4
            inner:
                addi a0, a0, 1
                addi a1, a1, 1
                addi a2, a2, 1
                addi t0, t0, -1
                bnez t0, inner
                addi a3, a3, 1
                addi a4, a4, 1
                addi s0, s0, -1
                bnez s0, outer
            """;

        var one = PipelineStats.Of(Go(nested, With(Predictor.OneBit)).Records);
        var two = PipelineStats.Of(Go(nested, With(Predictor.TwoBit)).Records);

        // Inner branch: 12 times, taken 9. Outer: 3 times, taken 2.
        Assert.Equal((15, 11), (one.Branches, one.BranchesTaken));
        Assert.Equal(8, one.Mispredictions);   // inner: 1 + 1, then 2 + 2 more; outer: first and last
        Assert.Equal(6, two.Mispredictions);   // inner: 1 + 1, then 1 + 1 more; outer: first and last
        Assert.True(two.Cycles < one.Cycles);
    }

    [Theory]
    [InlineData(Predictor.NotTaken)]
    [InlineData(Predictor.BackwardTaken)]
    [InlineData(Predictor.OneBit)]
    [InlineData(Predictor.TwoBit)]
    public void TheNumberOfWrongGuessesIsWhatTheRuleSaysItShouldBe(Predictor predictor)
    {
        // An independent statement of each rule, applied to the branches of the reference
        // machine in program order, must count the same wrong guesses as the pipeline did. The
        // programs keep two runs of any one branch at least four instructions apart, and are
        // short enough that no two branches share an entry, so timing cannot matter.
        string[] registers = ["a0", "a1", "a2", "a3"];
        var random = new SeededRandom(0xF37C_6401);

        for (var round = 0; round < 150; round++)
        {
            var lines = new List<string>();
            var loops = random.Next(1, 4);
            for (var loop = 0; loop < loops; loop++)
            {
                lines.Add($"li s{loop}, {random.Next(1, 6)}");
                lines.Add($"top{loop}:");
                for (var i = 0; i < random.Next(4, 8); i++)
                {
                    lines.Add($"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(-3, 3)}");
                }

                // A forward branch over two instructions, taken or not by the data.
                lines.Add($"{random.Pick(["beq", "bne", "blt", "bge"])} {random.Pick(registers)}, {random.Pick(registers)}, skip{loop}");
                lines.Add($"addi {random.Pick(registers)}, {random.Pick(registers)}, 1");
                lines.Add($"addi {random.Pick(registers)}, {random.Pick(registers)}, 1");
                lines.Add($"skip{loop}:");
                lines.Add("nop");
                if (random.NextBool())
                {
                    lines.Add($"j hop{loop}");
                    lines.Add("nop");
                    lines.Add($"hop{loop}:");
                    lines.Add("nop");
                }

                lines.Add("nop");
                lines.Add($"addi s{loop}, s{loop}, -1");
                lines.Add($"bnez s{loop}, top{loop}");
            }

            var source = string.Join('\n', lines);
            var program = AssemblerTesting.Assemble(source);
            var expected = CountWrongGuesses(program, predictor);

            foreach (var branches in Enum.GetValues<BranchDecision>())
            {
                var stats = PipelineStats.Of(Go(source, With(predictor, branches)).Records);
                if (stats.Mispredictions != expected)
                {
                    Assert.Fail(
                        $"seed {random.Seed:X}, round {round}, branches in {branches}: the pipeline guessed wrong " +
                        $"{stats.Mispredictions} times, the rule says {expected}\n{source}");
                }
            }
        }
    }

    /// <summary>
    /// The predictor rules, written a second time and differently: a dictionary by address
    /// instead of an indexed table, fed by the reference machine one branch at a time.
    /// </summary>
    private static int CountWrongGuesses(Program program, Predictor predictor)
    {
        var machine = new ReferenceMachine(program, TextWriter.Null);
        var remembered = new Dictionary<uint, (uint Target, int Confidence)>();
        var wrong = 0;

        while (!machine.IsFinished)
        {
            var commit = machine.Step();
            var control = commit.Instruction.Control;
            if (!control.IsControlFlow)
            {
                continue;
            }

            var pc = commit.Pc;
            var target = pc + (uint)commit.Instruction.Imm;
            var taken = !control.IsConditionalBranch || commit.NextPc != pc + 4;

            var guess = pc + 4;
            switch (predictor)
            {
                case Predictor.BackwardTaken:
                    if (control.Branch == BranchKind.Jal || (control.IsConditionalBranch && commit.Instruction.Imm < 0))
                    {
                        guess = target;
                    }

                    break;

                case Predictor.OneBit or Predictor.TwoBit:
                    var threshold = predictor == Predictor.OneBit ? 1 : 2;
                    if (remembered.TryGetValue(pc, out var entry) && entry.Confidence >= threshold)
                    {
                        guess = entry.Target;
                    }

                    break;
            }

            wrong += guess != commit.NextPc ? 1 : 0;

            if (predictor is Predictor.OneBit or Predictor.TwoBit)
            {
                if (remembered.TryGetValue(pc, out var known))
                {
                    var confidence = predictor == Predictor.OneBit
                        ? (taken ? 1 : 0)
                        : Math.Clamp(known.Confidence + (taken ? 1 : -1), 0, 3);
                    remembered[pc] = (taken ? commit.NextPc : known.Target, confidence);
                }
                else if (taken)
                {
                    remembered[pc] = (commit.NextPc, predictor == Predictor.OneBit ? 1 : 2);
                }
            }
        }

        return wrong;
    }

    [Theory]
    [InlineData(Predictor.OneBit)]
    [InlineData(Predictor.TwoBit)]
    public void EvenInATwoInstructionLoopThePredictorHasHeardInTime(Predictor predictor)
    {
        // The predictor learns how a branch came out at the end of the cycle that decided it,
        // which is after the same branch may have been fetched again. That never costs a guess.
        // A right guess only confirms what the entry already said, so hearing of it late changes
        // nothing; and a wrong guess sends fetch back, so the branch is not fetched again until
        // the cycle after the news has landed.
        const string tight = "li t0, 6\nloop:\naddi t0, t0, -1\nbnez t0, loop";
        var expected = CountWrongGuesses(AssemblerTesting.Assemble(tight), predictor);

        var inExecute = PipelineStats.Of(Go(tight, With(predictor)).Records);
        var inDecode = PipelineStats.Of(Go(tight, With(predictor, BranchDecision.Decode)).Records);

        Assert.Equal(2, expected);                    // the first time round, and the last
        Assert.Equal((6, 5), (inExecute.Branches, inExecute.BranchesTaken));
        Assert.Equal(expected, inExecute.Mispredictions);
        Assert.Equal(expected, inDecode.Mispredictions);
    }

    [Fact]
    public void TwoBranchesThatShareAnEntryPushEachOtherOut()
    {
        // The two inner loops' branches are 64 bytes apart: the same entry of a 16-entry buffer,
        // and different entries of a 64-entry one. Each loop goes round three times (taken,
        // taken, not taken) and the two take turns, six times over.
        var padding = string.Join("\n    ", Enumerable.Repeat("nop", 11));
        var source = $"""
                li   s0, 6
            outer:
                li   t0, 3
            first:
                addi t0, t0, -1
                nop
                nop
                bnez t0, first
                {padding}
                li   t1, 3
            second:
                addi t1, t1, -1
                nop
                nop
                bnez t1, second
                addi s0, s0, -1
                bnez s0, outer
            """;
        var program = AssemblerTesting.Assemble(source);
        Assert.Equal(64u, program.AddressOf("second") - program.AddressOf("first"));

        var small = PipelineStats.Of(Go(source, With(Predictor.TwoBit, btb: 16)).Records);
        var large = PipelineStats.Of(Go(source, With(Predictor.TwoBit, btb: 64)).Records);
        Assert.Equal(small.Instructions, large.Instructions);

        // With an entry each, a loop is guessed wrong twice on its first visit (not known yet,
        // then the way out) and once on every later visit (the way out only): 2 + 5 for each
        // loop, and 2 for the outer branch. Sharing an entry, each loop finds the other's there
        // on every visit, so every visit is a first visit: 2 * 6 for each loop, and 2.
        Assert.Equal(7 + 7 + 2, large.Mispredictions);
        Assert.Equal(12 + 12 + 2, small.Mispredictions);
        Assert.True(small.Cycles > large.Cycles);
    }

    [Fact]
    public void AnIndirectJumpIsGuessedToGoWhereItWentLastTime()
    {
        // A function called three times from one place, then once from another. Its return is
        // an indirect jump: guessed right while the caller stays the same, wrong when it changes.
        var run = Go("""
                li   s0, 3
            again:
                jal  ra, f
                addi a0, a0, 1
                addi s0, s0, -1
                bnez s0, again
                jal  ra, f
                j    done
            f:
                addi a1, a1, 1
                nop
                nop
                ret
            done:
                nop
            """, With(Predictor.TwoBit));

        // The return is the instruction at 0x28, decided in EX: its event is in the cycle it is there.
        var returns = run.Records
            .Where(r => r.Execute is { HasInstruction: true, Pc: 0x28 })
            .SelectMany(r => r.Events.OfType<BranchEvent>())
            .Select(b => (b.Target, b.Mispredicted))
            .ToList();

        // Three returns to 0x08, the first of them unknown; then one to 0x18, guessed as 0x08.
        Assert.Equal(
            [(8u, true), (8u, false), (8u, false), (0x18u, true)],
            returns);
        Assert.Equal(4u, run["a1"]);
    }

    [Theory]
    [InlineData(BranchDecision.Execute)]
    [InlineData(BranchDecision.Decode)]
    public void AStaleEntryThatSendsFetchOffAfterSomethingThatIsNotABranchIsPutRight(BranchDecision branches)
    {
        // On bare metal a program may rewrite itself. Here a branch is taken, so the buffer
        // remembers it; then the branch is overwritten with an addi and run again. The buffer
        // still says "taken", fetch goes off to the old target, and the mistake is caught where
        // a branch would have been decided.
        var bases = new AssemblerOptions { TextBase = 0x8000_0000, DataBase = 0x8000_4000 };
        var program = AssemblerTesting.Assemble("""
                la    t0, spot
                la    t1, replacement
                lw    t2, 0(t1)
                li    s0, 2
            spot:
                bnez  s0, landing            # taken the first time; an addi the second
                li    a1, 7                  # reached only once the branch is gone
                j     finish
            landing:
                addi  a0, a0, 1
                sw    t2, 0(t0)
                fence.i
                j     spot
            replacement:
                addi  a2, a2, 5
            finish:
                la    t3, tohost
                li    t4, 1
                sw    t4, 0(t3)
            .data
            tohost: .word 0, 0
            """, bases);

        var lockstep = new Lockstep(program, config: With(Predictor.OneBit, branches));
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < 5000)
        {
            records.Add(lockstep.Step());
        }

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        Assert.Equal((1u, 7u, 5u), (lockstep.Pipeline.Hart.X[10], lockstep.Pipeline.Hart.X[11], lockstep.Pipeline.Hart.X[12]));

        // The wrong guess is reported against an instruction that was not taken, because it
        // is not a branch at all.
        var spot = program.AddressOf("spot");
        var staleGuess = records
            .SelectMany(r => r.Events.OfType<BranchEvent>())
            .Last(b => records.Any(r => r.Events.Contains(b) && (r.Execute.Seq == b.Seq ? r.Execute.Pc : r.Decode.Pc) == spot));
        Assert.Equal((false, true), (staleGuess.Taken, staleGuess.Mispredicted));
    }

    [Fact]
    public void EveryExampleRunsInLockstepUnderEveryPredictorAndBothDecisions()
    {
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var program = AssemblerTesting.Assemble(File.ReadAllText(path));
            var name = Path.GetFileName(path);
            foreach (var predictor in Predictors)
            {
                foreach (var branches in Enum.GetValues<BranchDecision>())
                {
                    var lockstep = new Lockstep(program, new StringWriter(), config: With(predictor, branches, btb: 16));
                    lockstep.Pipeline.Recording = false;
                    lockstep.Run(5_000_000);

                    Assert.True(lockstep.Divergence is null, $"{name}, {predictor}, {branches}: {lockstep.Divergence?.Describe()}");
                    Assert.True(lockstep.Pipeline.IsFinished, $"{name}, {predictor}, {branches}");
                    Assert.Equal(Cli.RunTests.ExpectedOutput(name), lockstep.Pipeline.Hart.Output.ToString());
                }
            }
        }
    }

    [Fact]
    public void ABetterGuesserNeverMakesAProgramWrongOnlyFaster()
    {
        // The sieve: 149,814 instructions, most of them in two loops.
        var program = AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "primes.s")));
        var cycles = new Dictionary<Predictor, ulong>();
        foreach (var predictor in Predictors)
        {
            var machine = new PipelineMachine(program, new StringWriter(), config: With(predictor)) { Recording = false };
            machine.Run(5_000_000);
            Assert.Equal("1229\n", machine.Hart.Output.ToString());
            cycles[predictor] = machine.Cycles;
        }

        Assert.True(cycles[Predictor.BackwardTaken] < cycles[Predictor.NotTaken]);
        Assert.True(cycles[Predictor.TwoBit] < cycles[Predictor.NotTaken]);
        Assert.True(cycles[Predictor.TwoBit] <= cycles[Predictor.OneBit]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-16)]
    [InlineData(131072)]
    public void ABufferSizeThatIsNotAPowerOfTwoIsRefused(int entries)
    {
        var config = new PipelineConfig { Predictor = Predictor.TwoBit, BtbEntries = entries };

        Assert.Throws<ArgumentException>(() => new PipelineMachine(AssemblerTesting.Assemble("nop"), config: config));
    }

    [Fact]
    public void TheTraceShowsALoopThatHasBeenLearned()
    {
        var trace = RunOk("trace", Example("sum.s"), "--predictor", "2-bit", "--cycles", "20");

        Golden.Check("sum-2-bit.trace.txt", trace);
        Assert.EndsWith(" 40 instructions, 54 cycles, CPI 1.35, 0 stalls, 19 forwards, 4 flushes\n", trace);
    }

    [Fact]
    public void TheTraceSaysWhenAGuessOfTakenWasWrong()
    {
        var trace = RunOk("trace", Example("sum.s"), "--predictor", "backward-taken", "--from", "34", "--cycles", "8");

        Assert.Contains("ble (EX) is not taken, but was predicted taken; 2 instructions behind it are squashed", trace);
    }

    [Theory]
    [InlineData("--predictor", "psychic")]
    [InlineData("--btb", "100")]
    [InlineData("--btb", "0")]
    public void AnImpossibleSwitchIsRefused(string option, string value)
    {
        var (exitCode, _, error) = Run("trace", Example("sum.s"), option, value);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(value, error);
    }
}
