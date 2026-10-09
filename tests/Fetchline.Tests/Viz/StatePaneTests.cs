using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.State;

namespace Fetchline.Tests.Viz;

/// <summary>What the registers, memory and counters panes are given to draw.</summary>
public class StatePaneTests
{
    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    [Fact]
    public void TheRegistersAreNamedTheWayAskedAndTheOneJustWrittenIsMarked()
    {
        var session = new Session("li a0, 5\nli t0, 7\nnop\n");

        var reset = RegisterBank.Of(session);
        Assert.Equal(32, reset.Count);
        Assert.Equal(["zero", "ra", "sp", "gp"], reset.Take(4).Select(cell => cell.Name));
        Assert.Equal((2, "sp", 0x7FFF_FFF0u, false), (reset[2].Index, reset[2].Name, reset[2].Value, reset[2].Written));
        Assert.DoesNotContain(reset, cell => cell.Written);

        // The first li is in WB in cycle 5, the second in cycle 6, and the nop writes nothing.
        session.Seek(5);
        Assert.Equal([new RegisterCell(10, "a0", 5, Written: true)], RegisterBank.Of(session).Where(cell => cell.Written));
        session.Seek(6);
        Assert.Equal([new RegisterCell(5, "t0", 7, Written: true)], RegisterBank.Of(session).Where(cell => cell.Written));
        Assert.Equal(5u, RegisterBank.Of(session)[10].Value);
        session.Seek(7);
        Assert.DoesNotContain(RegisterBank.Of(session), cell => cell.Written);

        Assert.Equal(["x0", "x1", "x31"], new[] { 0, 1, 31 }.Select(i => RegisterBank.Of(session, RegisterStyle.Numeric)[i].Name));
    }

    [Fact]
    public void TheRegionsOfAProgramAreItsDataAndTheStack()
    {
        var session = new Session(".data\nfirst: .word 1, 2, 3\n.text\nnop\n");

        // Three words of data are three rows; the stack has nothing on it yet, and is shown as
        // the four words the first things pushed will go to.
        Assert.Equal(
            [new MemoryRegion("data", 0x1000_0000, 12), new MemoryRegion("stack", 0x7FFF_FFE0, 16)],
            MemoryDump.Regions(session));
        Assert.Equal(4, MemoryDump.BytesPerRow);

        // A program with no data has only the stack; a source that does not assemble, nothing.
        Assert.Equal(["stack"], MemoryDump.Regions(new Session("nop")).Select(region => region.Name));
        Assert.Empty(MemoryDump.Regions(new Session("bogus")));
    }

    [Fact]
    public void TheStackIsShownFromTheStackPointerUp()
    {
        var session = new Session(Example("factorial.s"));
        session.Seek(ulong.MaxValue);
        var deepest = Enumerable.Range(0, (int)session.Cycle + 1).Min(cycle =>
        {
            session.Seek((ulong)cycle);
            return session.Registers[2];
        });

        // Ten calls of sixteen bytes each. At the deepest point the region begins at the stack
        // pointer and ends where the stack began.
        Assert.Equal(0x7FFF_FFF0u - 160, deepest);
        session.Seek(0);
        while (session.Registers[2] != deepest)
        {
            session.Step();
        }

        var stack = MemoryDump.Regions(session).Single(region => region.Name == "stack");
        Assert.Equal((deepest, 160u), (stack.Start, stack.Length));
        Assert.Equal(40, MemoryDump.Rows(session, stack).Count);
    }

    [Fact]
    public void ARowIsAWordItsTextAndWhichOfItsBytesWereJustStored()
    {
        var session = new Session("""
            .data
            text: .ascii "Hi, RV32"
            cell: .word 0x11223344, 0
            .text
                lui  s0, 0x10000
                li   t0, 0x7A
                nop
                nop
                sh   t0, 13(s0)
            """);
        var data = MemoryDump.Regions(session)[0];

        // Four rows: the text in two, then the word, low byte first as it lies in memory, then zero.
        var before = MemoryDump.Rows(session, data);
        Assert.Equal(
            [(0x1000_0000u, "Hi, "), (0x1000_0004u, "RV32"), (0x1000_0008u, "D3\"."), (0x1000_000Cu, "....")],
            before.Select(row => (row.Address, row.Text)));
        Assert.Equal([0x48, 0x69, 0x2C, 0x20], before[0].Bytes.Select(b => (int)b));
        Assert.Equal([0x44, 0x33, 0x22, 0x11], before[2].Bytes.Select(b => (int)b));
        Assert.DoesNotContain(before.SelectMany(row => row.Written), written => written);

        // The store is the fifth instruction, in MEM in cycle 8: two bytes, at 13 and 14.
        session.Seek(8);
        var after = MemoryDump.Rows(session, data);
        Assert.Equal([0x00, 0x7A, 0x00, 0x00], after[3].Bytes.Select(b => (int)b));
        Assert.Equal([false, true, true, false], after[3].Written);
        Assert.Equal(".z..", after[3].Text);
        Assert.DoesNotContain(after.Take(3).SelectMany(row => row.Written), written => written);

        // A cycle later the bytes are as stored and nothing is marked.
        session.Seek(9);
        Assert.DoesNotContain(MemoryDump.Rows(session, data).SelectMany(row => row.Written), written => written);
        Assert.Equal(3, MemoryDump.Rows(session, 0x1000_0000, 3).Count);
    }

    [Fact]
    public void TheCountersOfTheSumAreTheOnesTheComparisonTableHas()
    {
        var session = new Session(Example("sum.s"));
        session.Seek(ulong.MaxValue);

        // Forty instructions in 68 cycles; nine taken branches of ten, each a wrong guess and a
        // flush; two system calls with something behind them. The branch takes the counter from
        // the addi just ahead of it every time round, ten forwards from EX/MEM, and the very
        // first add takes it from the li two ahead, one from MEM/WB.
        Assert.Equal(
            [
                "cycles 68", "instructions 40", "CPI 1.70", "stalls 0", "flushes 11", "- branch 9", "- system instruction 2",
                "squashed 23", "forwards 11", "- EX/MEM 10", "- MEM/WB 1", "branches 10", "- taken 9", "- wrong guesses 9",
                "loads 0", "stores 0",
            ],
            Counters.Of(session.Stats, EnglishMessages.Instance).Select(line => (line.Part ? "- " : string.Empty) + line.Label + " " + line.Value));
    }

    [Fact]
    public void ACauseThatHasNotHappenedIsLeftOut()
    {
        var reset = Counters.Of(new Session("nop").Stats, EnglishMessages.Instance);
        Assert.Equal(
            ["cycles 0", "instructions 0", "CPI 0.00", "stalls 0", "flushes 0", "squashed 0", "forwards 0", "branches 0", "loads 0", "stores 0"],
            reset.Select(line => line.Label + " " + line.Value));
        Assert.DoesNotContain(reset, line => line.Part);

        // Every cause there is has a name, and no two the same.
        var messages = EnglishMessages.Instance;
        Assert.Equal(
            ["load-use", "no forwarding", "branch in ID", "multi-cycle", "instruction cache miss", "data cache miss"],
            Enum.GetValues<StallCause>().Select(messages.NameOf));
        Assert.Equal(["branch", "system instruction", "trap", "stop"], Enum.GetValues<FlushCause>().Select(messages.NameOf));
    }

    [Fact]
    public void EveryKindOfStallAndFlushShowsUpInItsCounter()
    {
        static List<string> Parts(string source, PipelineConfig config)
        {
            var session = new Session(source, config);
            session.Seek(ulong.MaxValue);
            return [.. Counters.Of(session.Stats, EnglishMessages.Instance).Where(line => line.Part).Select(line => line.Label + " " + line.Value)];
        }

        Assert.Contains("load-use 1", Parts(Example("load-use.s"), PipelineConfig.Default));
        Assert.Contains("MEM/WB 1", Parts(Example("load-use.s"), PipelineConfig.Default));
        Assert.Contains("no forwarding 2", Parts("li a0, 5\naddi a1, a0, 1", new PipelineConfig { Hazards = HazardHandling.StallOnly }));
        Assert.Contains("branch in ID 1", Parts("li t0, 1\nbnez t0, over\nnop\nover:\nnop", new PipelineConfig { Branches = BranchDecision.Decode }));
        Assert.Contains("multi-cycle 6", Parts(Example("multiply.s"), new PipelineConfig { MulDivCycles = 4 }));
        Assert.Contains("stop 1", Parts("li a7, 10\necall\nnop", PipelineConfig.Default));
    }
}
