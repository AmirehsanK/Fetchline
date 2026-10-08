using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Cli;

public class RunTests
{
    /// <summary>What each example prints. A new example needs a row here before the tests pass.</summary>
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["hello.s"] = "Hello, RISC-V!\n",
        ["sum.s"] = "55\n",
        ["fib.s"] = "0 1 1 2 3 5 8 13 21 34 \n",
        ["factorial.s"] = "3628800\n",
        ["bubble-sort.s"] = "1 2 3 5 7 8 9 \n",
        ["gcd.s"] = "21\n",
        ["load-use.s"] = string.Empty,
        ["primes.s"] = "1229\n",
    };

    public static TheoryData<string> Examples() => AsmAndDisTests.Examples();

    /// <summary>What an example prints, for the tests that run the examples another way.</summary>
    internal static string ExpectedOutput(string name) => Expected[name];

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryExamplePrintsWhatItShould(string name)
    {
        Assert.True(Expected.TryGetValue(name, out var expected), $"say what examples/{name} prints in RunTests.Expected");

        Assert.Equal(expected, RunOk("run", Example(name)));
    }

    [Fact]
    public void AnExampleRunsTheSameFromItsElfFile()
    {
        using var elf = new TemporaryFile(extension: ".elf");
        RunOk("asm", Example("bubble-sort.s"), "-o", elf.Path);

        Assert.Equal(Expected["bubble-sort.s"], RunOk("run", elf.Path));
    }

    [Theory]
    [InlineData("li a7, 10\necall", 0)]
    [InlineData("li a0, 3\nli a7, 93\necall", 3)]
    [InlineData("li a0, 256 + 7\nli a7, 93\necall", 7)]     // a shell keeps the low eight bits
    [InlineData("li a0, -1\nli a7, 93\necall", 255)]
    public void TheExitCodeIsTheProgramsOwn(string program, int exitCode)
    {
        using var source = Source(program);

        var (actual, output, error) = Run("run", source.Path);

        Assert.Equal((exitCode, string.Empty, string.Empty), (actual, output, error));
    }

    [Fact]
    public void AFaultIsReportedWithTheLineThatCausedIt()
    {
        using var source = Source("main:\n    li   a0, 5\n    sw   a0, 0(zero)      # oops\n    li   a0, 6\n");

        var (exitCode, output, error) = Run("run", source.Path);

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Equal(
            "fetchline: the program stopped: a store to 0x00000000, which is in 'text' and cannot be written, at pc 0x00000004\n" +
            $"  {source.Path}:3: sw   a0, 0(zero)\n",
            error);
    }

    [Fact]
    public void OutputPrintedBeforeAFaultIsStillShown()
    {
        using var source = Source("li a0, 7\nli a7, 1\necall\n.word 0\n");

        var (exitCode, output, error) = Run("run", source.Path);

        Assert.Equal((1, "7"), (exitCode, output));
        Assert.StartsWith("fetchline: the program stopped: an illegal instruction (0x00000000), at pc 0x0000000c\n", error);
    }

    [Fact]
    public void AProgramThatNeverEndsIsStoppedAtTheLimit()
    {
        using var source = Source("loop: j loop\n");

        var (exitCode, _, error) = Run("run", source.Path, "--max-instructions", "5000");

        Assert.Equal(1, exitCode);
        Assert.Equal("fetchline: still running after 5000 instructions; --max-instructions raises the limit\n", error);
    }

    [Fact]
    public void AnEbreakPausesAndSaysWhere()
    {
        using var source = Source("li a0, 1\nebreak\nli a0, 2\n");

        var (exitCode, _, error) = Run("run", source.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal($"fetchline: paused at an ebreak at pc 0x00000004\n  {source.Path}:2: ebreak\n", error);
    }

    [Fact]
    public void TheRegistersAndTheCountCanBePrinted()
    {
        using var source = Source("li a0, 0x1234\nli s11, -1\nli t6, 6\n");

        var (exitCode, output, error) = Run("run", source.Path, "--registers", "--stats");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            """
             zero 00000000     s0 00000000     a6 00000000     s8 00000000
               ra 00000010     s1 00000000     a7 00000000     s9 00000000
               sp 7ffffff0     a0 00001234     s2 00000000    s10 00000000
               gp 10000000     a1 00000000     s3 00000000    s11 ffffffff
               tp 00000000     a2 00000000     s4 00000000     t3 00000000
               t0 00000000     a3 00000000     s5 00000000     t4 00000000
               t1 00000000     a4 00000000     s6 00000000     t5 00000000
               t2 00000000     a5 00000000     s7 00000000     t6 00000006
               pc 00000010

            """.ReplaceLineEndings("\n"),
            output);
        Assert.Equal("fetchline: 4 instructions\n", error);
    }

    [Fact]
    public void ASourceWithErrorsIsNotRun()
    {
        using var source = Source("li a0, 7\nli a7, 1\necall\nbogus\n");

        var (exitCode, output, error) = Run("run", source.Path);

        Assert.Equal((1, string.Empty), (exitCode, output));
        Assert.Contains("unknown instruction 'bogus'", error);
    }
}
