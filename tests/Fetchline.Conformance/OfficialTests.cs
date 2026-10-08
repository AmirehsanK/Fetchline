using Fetchline.Cli.Commands;
using Fetchline.Core.Elf;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;
using Fetchline.Tests;
using Fetchline.Tests.Cli;
using Fetchline.Tests.Support;

namespace Fetchline.Conformance;

/// <summary>
/// The official RISC-V tests, <c>riscv-software-src/riscv-tests</c>, built by GCC on a GitHub
/// runner from a pinned commit (see <c>tests/vectors/PROVENANCE.md</c>). Each is a bare-metal
/// program that checks its own results and writes a verdict to <c>tohost</c>.
/// </summary>
public class OfficialTests
{
    private const ulong Budget = 5_000_000;

    private static readonly string ExclusionsFile = Repo.PathOf("tests", "conformance-exclusions.txt");

    private static readonly Dictionary<string, string> Excluded =
        TestCommand.ParseExclusions(File.ReadAllText(ExclusionsFile));

    public static TheoryData<string> Names() => Vectors.Rows();

    [Theory]
    [MemberData(nameof(Names))]
    public void OnTheReferenceMachine(string name)
    {
        var machine = new ReferenceMachine(ElfFile.Read(Vectors.Elf(name)), TextWriter.Null);
        Assert.Equal(ExecutionEnvironment.Bare, machine.Hart.Environment);

        var verdict = TestVerdict.Of(machine.Run(Budget));

        if (Excluded.TryGetValue(name, out var reason))
        {
            // If this starts passing, the exclusion is out of date and its line has to go.
            Assert.False(verdict.Passed, $"{name} passes, but is excluded because it {reason}");
        }
        else
        {
            Assert.True(verdict.Passed, $"{name}: {verdict.Summary}");
        }
    }

    [Fact]
    public void TheThreeSuitesAreComplete()
    {
        var names = Vectors.Names;

        Assert.Equal(42, names.Count(n => n.StartsWith("rv32ui-p-", StringComparison.Ordinal)));
        Assert.Equal(8, names.Count(n => n.StartsWith("rv32um-p-", StringComparison.Ordinal)));
        Assert.Equal(16, names.Count(n => n.StartsWith("rv32mi-p-", StringComparison.Ordinal)));
    }

    [Fact]
    public void OnlyMachineModeTestsThatNeedAMissingFeatureAreExcluded()
    {
        // Every user-level test must pass: the base instruction set and M have no excuses.
        Assert.All(Excluded.Keys, name =>
        {
            Assert.StartsWith("rv32mi-p-", name);
            Assert.Contains(name, Vectors.Names);
        });
        Assert.All(Excluded.Values, reason => Assert.StartsWith("needs ", reason));
        Assert.Equal(2, Excluded.Count);
    }

    [Fact]
    public void TheCommandLineRunsTheFolderAndAgrees()
    {
        var folder = Repo.PathOf("tests", "vectors", "riscv-tests", "elf");

        var (exitCode, output, error) = CommandLine.Run("test", folder, "--exclusions", ExclusionsFile);

        Assert.Equal(string.Empty, error);
        Assert.Equal(
            "rv32mi-p-breakpoint          excluded: needs the debug trigger registers (tselect, tdata1), which this core does not have\n" +
            "rv32mi-p-pmpaddr             excluded: needs physical memory protection (pmpaddr0, pmpcfg0), which this core does not have\n" +
            "64 passed, 0 failed, 2 excluded\n",
            output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void WithoutTheExclusionsTheCommandLineReportsTheTwoFailures()
    {
        var folder = Repo.PathOf("tests", "vectors", "riscv-tests", "elf");

        var (exitCode, output, _) = CommandLine.Run("test", folder);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "rv32mi-p-breakpoint          FAIL: test case 2 failed\n" +
            "rv32mi-p-pmpaddr             FAIL: test case 1 failed\n" +
            "64 passed, 2 failed, 0 excluded\n",
            output);
    }
}
