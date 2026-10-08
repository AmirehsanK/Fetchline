using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;

namespace Fetchline.Tests.Machine;

/// <summary>What a finished run looks like to a test.</summary>
internal sealed record RunResult(ReferenceMachine Machine, IReadOnlyList<Commit> Commits, string Output)
{
    public Commit Last => Commits[^1];

    public Hart Hart => Machine.Hart;

    /// <summary>The final value of a register, by either of its names.</summary>
    public uint this[string register]
    {
        get
        {
            Assert.True(Registers.TryParse(register, out var index), $"'{register}' is not a register");
            return Hart.X[index];
        }
    }
}

internal static class MachineTesting
{
    /// <summary>Assembles a program and runs it on the reference machine until it stops.</summary>
    public static RunResult Run(string source, int maxInstructions = 100_000)
    {
        var output = new StringWriter();
        var machine = new ReferenceMachine(AssemblerTesting.Assemble(source), output);
        var commits = new List<Commit>();

        while (commits.Count < maxInstructions)
        {
            var commit = machine.Step();
            commits.Add(commit);
            if (commit.Stop != StopReason.None)
            {
                break;
            }
        }

        return new RunResult(machine, commits, output.ToString());
    }

    /// <summary>Runs a program that must reach the end of its code without stopping for any other reason.</summary>
    public static RunResult RunToEnd(string source)
    {
        var result = Run(source);
        Assert.True(
            result.Last.Stop == StopReason.EndOfProgram,
            $"stopped with {result.Last.Stop}: {result.Last.Message}");
        return result;
    }
}
