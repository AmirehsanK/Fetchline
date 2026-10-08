using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Support;
using Fetchline.Viz;

namespace Fetchline.Tests.Viz;

/// <summary>
/// The session behind the playground: a run that can be stepped forwards and backwards, whose
/// registers, memory and console are made from the cycle records alone.
/// </summary>
public class SessionTests
{
    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    /// <summary>Everything a session shows at one cycle, for comparing one visit with another.</summary>
    private sealed record Shown(ulong Cycle, string Registers, string Output, string Stats, string Stack, StopReason Stopped)
    {
        public static Shown Of(Session session)
        {
            var stats = session.Stats;
            return new Shown(
                session.Cycle,
                string.Join(' ', session.Registers.ToArray().Select(value => value.ToString("x"))),
                session.Output,
                $"{stats.Cycles} {stats.Instructions} {stats.Stalls} {stats.Forwards} {stats.Flushes} {stats.Squashed} {stats.Loads} {stats.Stores}",
                string.Join(' ', Enumerable.Range(0, 24).Select(i => session.ReadWord(0x7FFF_FFF0 - (uint)(4 * i)).ToString("x"))),
                session.Stopped);
        }
    }

    /// <summary>Runs a session to its end, noting what it shows at reset and after every cycle.</summary>
    private static List<Shown> Walk(Session session)
    {
        var shown = new List<Shown> { Shown.Of(session) };
        while (session.Step())
        {
            shown.Add(Shown.Of(session));
            Assert.True(shown.Count < 1_000_000, "the run did not end");
        }

        return shown;
    }

    [Fact]
    public void AtResetNothingHasRunAndTheRegistersAreWhatTheSurroundingsSetUp()
    {
        var session = new Session("nop\nnop");

        Assert.Equal((0ul, 0ul, 1ul), (session.Cycle, session.Frontier, session.FirstKept));
        Assert.Null(session.Record);
        Assert.Equal((true, false, false), (session.CanStep, session.CanStepBack, session.IsFinished));
        Assert.Equal(StopReason.None, session.Stopped);
        Assert.Equal(string.Empty, session.Output);
        Assert.Equal(0ul, session.Stats.Cycles);
        Assert.Empty(session.Records(0, 100));

        // ra is the address just past the code, sp the top of the stack, gp the start of the data.
        Assert.Equal((8u, 0x7FFF_FFF0u, 0x1000_0000u), (session.Registers[1], session.Registers[2], session.Registers[3]));

        // The program is in memory before anything runs: two nops.
        Assert.Equal((0x0000_0013u, 0x0000_0013u, 0u), (session.ReadWord(0), session.ReadWord(4), session.ReadWord(8)));
        Assert.False(session.StepBack());
    }

    [Fact]
    public void SteppingShowsEachCycleInTurnUntilTheRunEnds()
    {
        var session = new Session(Example("load-use.s"));

        for (ulong cycle = 1; cycle <= 8; cycle++)
        {
            Assert.False(session.IsFinished);
            Assert.True(session.Step());
            Assert.Equal((cycle, cycle, cycle), (session.Cycle, session.Frontier, session.Record!.Cycle));
        }

        Assert.Equal(StopReason.EndOfProgram, session.Stopped);
        Assert.Equal((true, false, true), (session.IsFinished, session.CanStep, session.CanStepBack));
        Assert.False(session.Step());
        Assert.Equal(8ul, session.Cycle);

        var stats = session.Stats;
        Assert.Equal((8ul, 3ul, 1, 2), (stats.Cycles, stats.Instructions, stats.Stalls, stats.Forwards));
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul, 7ul, 8ul], session.Records(1, 99).Select(record => record.Cycle));
        Assert.Equal([3ul, 4ul], session.Records(3, 4).Select(record => record.Cycle));
    }

    [Theory]
    [InlineData("bubble-sort.s")]
    [InlineData("factorial.s")]
    [InlineData("hello.s")]
    [InlineData("gcd.s")]
    [InlineData("multiply.s")]
    public void WhatIsShownIsTheMachinesOwnStateAtEveryCycle(string example)
    {
        // The session never looks inside the machine: its registers and memory are folded up
        // from records. Here a second machine runs the same program, and is looked inside.
        var session = new Session(Example(example));
        var output = new StringWriter();
        var machine = new PipelineMachine(session.Program!, output);

        while (!machine.IsFinished)
        {
            machine.Step();
            Assert.True(session.Step());

            Assert.True(session.Registers.SequenceEqual(machine.Hart.X), $"registers differ after cycle {session.Cycle}");
            Assert.Equal(output.ToString(), session.Output);
            if (session.Cycle % 61 == 0 || machine.IsFinished)
            {
                AssertSameMemory(machine, session);
            }
        }

        Assert.True(session.IsFinished);
        Assert.Equal(machine.Stopped, session.Stopped);
        Assert.Equal(Cli.RunTests.ExpectedOutput(example), session.Output);
    }

    [Fact]
    public void WhatIsShownIsTheMachinesOwnStateOnRandomProgramsBuiltEveryWay()
    {
        var random = new SeededRandom(0xF37C_7101);
        var configurations = Comparison.Configurations();

        for (var round = 0; round < 120; round++)
        {
            var source = ProgramGenerator.Generate(random);
            var config = configurations[round % configurations.Count] with { MulDivCycles = 1 + (round % 3) };
            var session = new Session(source, config);
            var output = new StringWriter();
            var machine = new PipelineMachine(session.Program!, output, config: config);

            for (var cycle = 0; cycle < 20_000 && !machine.IsFinished; cycle++)
            {
                machine.Step();
                Assert.True(session.Step());
                if (!session.Registers.SequenceEqual(machine.Hart.X))
                {
                    Assert.Fail($"seed {random.Seed:X}, round {round}, cycle {session.Cycle}: the registers differ\n{source}");
                }
            }

            AssertSameMemory(machine, session);
            Assert.Equal(output.ToString(), session.Output);
            Assert.Equal(machine.Cycles, session.Cycle);
        }
    }

    private static void AssertSameMemory(PipelineMachine machine, Session session)
    {
        foreach (var page in machine.Hart.Memory.PageAddresses)
        {
            for (uint offset = 0; offset < 4096; offset += 4)
            {
                if (machine.Hart.Memory.ReadU32(page + offset) != session.ReadWord(page + offset))
                {
                    Assert.Fail($"memory differs at 0x{page + offset:x8} after cycle {session.Cycle}");
                }
            }
        }
    }

    [Theory]
    [InlineData("factorial.s")]
    [InlineData("bubble-sort.s")]
    [InlineData("hello.s")]
    public void SteppingBackShowsExactlyWhatWasThereBefore(string example)
    {
        var session = new Session(Example(example));
        var forwards = Walk(session);
        var last = session.Cycle;

        // All the way back, one cycle at a time, and all the way forward again.
        for (var cycle = (int)last - 1; cycle >= 0; cycle--)
        {
            Assert.True(session.StepBack());
            Assert.Equal(forwards[cycle], Shown.Of(session));
        }

        Assert.Equal(last, session.Frontier);       // stepping back does not undo the running
        Assert.False(session.StepBack());

        for (var cycle = 1; cycle <= (int)last; cycle++)
        {
            Assert.True(session.Step());
            Assert.Equal(forwards[cycle], Shown.Of(session));
        }

        Assert.True(session.IsFinished);
        Assert.False(session.Step());
    }

    [Fact]
    public void AnyCycleCanBeShownInAnyOrder()
    {
        var session = new Session(Example("factorial.s"));
        var forwards = Walk(session);
        var last = (int)session.Cycle;
        var random = new SeededRandom(0xF37C_7102);

        for (var visit = 0; visit < 300; visit++)
        {
            var cycle = random.Next(0, last);
            session.Seek((ulong)cycle);
            Assert.Equal(forwards[cycle], Shown.Of(session));
        }

        // Past the end is the end; and reset is cycle zero.
        session.Seek(ulong.MaxValue);
        Assert.Equal(forwards[last], Shown.Of(session));
        session.Reset();
        Assert.Equal(forwards[0], Shown.Of(session));
    }

    [Fact]
    public void ACycleNotRunYetIsRunTo()
    {
        var session = new Session(Example("sum.s"));

        session.Seek(30);

        Assert.Equal((30ul, 30ul), (session.Cycle, session.Frontier));
        Assert.Equal(30ul, session.Stats.Cycles);
        Assert.False(session.IsFinished);
    }

    [Fact]
    public void OnlyTheLatestRecordsAreKeptAndGoingBackPastThemIsARunFromReset()
    {
        var whole = new Session(Example("bubble-sort.s"));
        var forwards = Walk(whole);
        var last = whole.Cycle;
        Assert.True(last > 300, $"only {last} cycles");
        Assert.Equal(1ul, whole.FirstKept);

        var session = new Session(Example("bubble-sort.s"), history: 64);
        while (session.Step())
        {
        }

        // At least the last 64 cycles are there, and not many more.
        var kept = session.Records(1, last);
        Assert.Equal((last, last), (session.Cycle, kept[^1].Cycle));
        Assert.Equal(session.FirstKept, kept[0].Cycle);
        Assert.InRange(kept.Count, 64, 64 + 16);
        Assert.Equal(forwards[^1], Shown.Of(session));

        // Back within what is kept: no running needed, and the frontier stays where it was.
        session.Seek(last - 20);
        Assert.Equal(forwards[(int)last - 20], Shown.Of(session));
        Assert.Equal(last, session.Frontier);

        // Back past it: the run is made again from reset, as far as the cycle asked for.
        session.Seek(5);
        Assert.Equal(forwards[5], Shown.Of(session));
        Assert.Equal((5ul, 1ul), (session.Frontier, session.FirstKept));

        // And on to the end again: the same end, with nothing printed twice.
        while (session.Step())
        {
        }

        Assert.Equal(forwards[^1], Shown.Of(session));
        Assert.Equal("1 2 3 5 7 8 9 \n", session.Output);

        // The counters cover the whole run, whatever has been dropped.
        Assert.Equal(last, session.Stats.Cycles);
    }

    [Fact]
    public void ARunGoesInSlicesAndStopsAtAPause()
    {
        var session = new Session("li a0, 1\nebreak\nli a0, 2\nli a1, 3");

        Assert.Equal(3, session.Run(3));
        Assert.Equal(3ul, session.Cycle);

        // The ebreak leaves WB in cycle 6. The instruction after it has not been fetched again yet.
        Assert.Equal(3, session.Run(1000));
        Assert.Equal((6ul, StopReason.Breakpoint), (session.Cycle, session.Stopped));
        Assert.Equal((false, true), (session.IsFinished, session.CanStep));
        Assert.Equal((1u, 0u), (session.Registers[10], session.Registers[11]));

        // Run again and it carries on from the pause to the end.
        Assert.True(session.Run(1000) > 0);
        Assert.Equal(StopReason.EndOfProgram, session.Stopped);
        Assert.Equal((2u, 3u), (session.Registers[10], session.Registers[11]));
        Assert.Equal(0, session.Run(1000));
    }

    [Fact]
    public void AStoreIsTakenBackOutOfMemoryWhenTheRunIsSteppedBack()
    {
        var session = new Session(".data\ncell: .word 0x11111111\n.text\nlui s0, 0x10000\nli t0, 0x55\nnop\nnop\nsb t0, 1(s0)\n");
        Assert.Equal(0x1111_1111u, session.ReadWord(0x1000_0000));

        // The store is the fifth instruction, in MEM in cycle 8.
        session.Seek(7);
        Assert.Equal(0x1111_1111u, session.ReadWord(0x1000_0000));
        session.Step();
        Assert.Equal(0x1111_5511u, session.ReadWord(0x1000_0000));
        Assert.Equal(0x55, session.ReadByte(0x1000_0001));

        session.StepBack();
        Assert.Equal(0x1111_1111u, session.ReadWord(0x1000_0000));
        session.Seek(ulong.MaxValue);
        Assert.Equal(0x1111_5511u, session.ReadWord(0x1000_0000));
        Assert.Equal(1, session.Stats.Stores);
    }

    [Fact]
    public void TheConsoleShowsOnlyWhatHadBeenPrintedByTheCycleShown()
    {
        var session = new Session(Example("sum.s"));
        while (session.Step())
        {
        }

        Assert.Equal("55\n", session.Output);
        var end = session.Cycle;

        // Each of the two prints takes effect in MEM. Step back until the newline has not been
        // printed, then until nothing has.
        while (session.Output == "55\n")
        {
            session.StepBack();
        }

        Assert.Equal("55", session.Output);
        while (session.Output == "55")
        {
            session.StepBack();
        }

        Assert.Equal(string.Empty, session.Output);
        Assert.True(session.Cycle > 40 && session.Cycle < end);
        session.Seek(end);
        Assert.Equal("55\n", session.Output);
    }

    [Fact]
    public void WithHazardHandlingOffTheFirstWrongValueAppearsWhenTheRunGetsThere()
    {
        var session = new Session(Example("sum.s"), new PipelineConfig { Hazards = HazardHandling.Off });

        session.Seek(7);
        Assert.Null(session.Divergence);

        session.Step();
        Assert.Equal((4ul, 8ul), (session.Divergence!.Index, session.Divergence.Cycle));

        session.StepBack();
        Assert.Null(session.Divergence);

        // The run goes on to its own, wrong, end.
        session.Seek(ulong.MaxValue);
        Assert.Equal("65\n", session.Output);
        Assert.NotNull(session.Divergence);
    }

    [Fact]
    public void AFaultIsTheEndOfTheRunAndSaysWhatHappened()
    {
        var session = new Session("li a0, 5\nsw a0, 0(zero)\nli a1, 1");

        session.Seek(ulong.MaxValue);

        Assert.Equal((StopReason.Fault, true), (session.Stopped, session.IsFinished));
        Assert.StartsWith("a store to 0x00000000", session.Record!.Commit!.Value.Message);
        Assert.Equal(0u, session.Registers[11]);
    }

    [Fact]
    public void ASourceWithErrorsHasNothingToRunAndSaysWhatIsWrong()
    {
        var session = new Session("li a0, 5\nadid a0, a0, 1\n");

        Assert.Null(session.Program);
        Assert.Equal((false, false, false), (session.CanStep, session.CanStepBack, session.IsFinished));
        Assert.False(session.Step());
        Assert.Equal(0, session.Run(10));
        session.Seek(5);
        Assert.Equal(0ul, session.Cycle);
        Assert.Null(session.Record);

        var diagnostic = Assert.Single(session.Assembly.Diagnostics);
        Assert.Equal(("unknown instruction 'adid'", 2), (diagnostic.Message, diagnostic.Span.Line));
        Assert.Equal("did you mean 'addi'?", diagnostic.Hint);
    }

    [Fact]
    public void AnImpossibleConfigurationOrHistoryIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new Session("nop", new PipelineConfig { BtbEntries = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Session("nop", history: 0));
    }

    [Fact]
    public void TheSessionRemembersWhatItWasGiven()
    {
        var config = new PipelineConfig { Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit };
        var session = new Session("li a0, 1", config);

        Assert.Equal(("li a0, 1", config), (session.Source, session.Config));
        Assert.True(session.Assembly.Success);
        Assert.Equal(PipelineConfig.Default, new Session("nop").Config);
    }
}
