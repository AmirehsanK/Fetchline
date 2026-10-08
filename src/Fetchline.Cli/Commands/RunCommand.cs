using System.CommandLine;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;

namespace Fetchline.Cli.Commands;

/// <summary><c>fetchline run</c>: run a program on the reference machine and show what it prints.</summary>
internal static class RunCommand
{
    private const ulong DefaultBudget = 100_000_000;

    public static Command Build()
    {
        var file = new Argument<FileInfo>("file") { Description = "An assembly source, or an ELF executable." };
        var budget = new Option<ulong>("--max-instructions")
        {
            Description = "Stop after this many instructions, in case the program never ends.",
            DefaultValueFactory = _ => DefaultBudget,
        };
        var registers = new Option<bool>("--registers", "-r") { Description = "Print the registers when the program stops." };
        var stats = new Option<bool>("--stats", "-s") { Description = "Print how many instructions ran." };

        var command = new Command("run", "Run a program and show its output.") { file, budget, registers, stats };

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
            var source = parse.GetValue(file)!;
            var program = ProgramFile.Load(source, stderr, out var exitCode);
            if (program is null)
            {
                return exitCode;
            }

            var machine = new ReferenceMachine(program, stdout);
            var last = machine.Run(parse.GetValue(budget));
            stdout.Flush();

            exitCode = Report(last, machine, program, source, stderr);
            if (parse.GetValue(registers))
            {
                WriteRegisters(machine.Hart, stdout);
            }

            if (parse.GetValue(stats))
            {
                stderr.WriteLine($"fetchline: {machine.Hart.InstructionsRetired} instructions");
            }

            return exitCode;
        });

        return command;
    }

    /// <summary>Says why the program stopped, when that is not simply that it finished.</summary>
    private static int Report(Commit last, ReferenceMachine machine, Program program, FileInfo source, TextWriter stderr)
    {
        switch (last.Stop)
        {
            case StopReason.Exit:
                // A shell keeps only the low eight bits of an exit code.
                return last.ExitCode & 0xFF;

            case StopReason.EndOfProgram:
                return FetchlineCommand.Ok;

            case StopReason.Breakpoint:
                stderr.WriteLine($"fetchline: paused at an ebreak at pc 0x{last.Pc:x8}");
                WriteSourceLine(last.Pc, program, source, stderr);
                return FetchlineCommand.Ok;

            case StopReason.Fault:
                stderr.WriteLine($"fetchline: the program stopped: {last.Message}");
                WriteSourceLine(last.Pc, program, source, stderr);
                return FetchlineCommand.Failed;

            default:
                stderr.WriteLine(
                    $"fetchline: still running after {machine.Hart.InstructionsRetired} instructions; " +
                    "--max-instructions raises the limit");
                return FetchlineCommand.Failed;
        }
    }

    private static void WriteSourceLine(uint pc, Program program, FileInfo source, TextWriter stderr)
    {
        if (program.Source is { } text && program.SourceMap.TryGetByAddress(pc, out var entry))
        {
            stderr.WriteLine($"  {source}:{entry.Line}: {text.Substring(entry.Start, entry.Length)}");
        }
    }

    private static void WriteRegisters(Hart hart, TextWriter stdout)
    {
        for (var row = 0; row < Registers.Count / 4; row++)
        {
            // Down the columns, so that neighbours in the register file stay neighbours.
            var cells = Enumerable.Range(0, 4)
                .Select(column => row + (column * (Registers.Count / 4)))
                .Select(index => $"{Registers.Name(index),5} {hart.X[index]:x8}");
            stdout.WriteLine(string.Join("  ", cells));
        }

        stdout.WriteLine($"{"pc",5} {hart.Pc:x8}");
    }
}
