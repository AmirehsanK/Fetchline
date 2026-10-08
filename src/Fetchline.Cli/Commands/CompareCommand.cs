using System.CommandLine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;

namespace Fetchline.Cli.Commands;

/// <summary>
/// <c>fetchline compare</c>: run a program on the pipeline built every way, and print one table
/// of how long each took and what the cycles were lost to.
/// </summary>
internal static class CompareCommand
{
    private const ulong DefaultBudget = 2_000_000;

    public static Command Build()
    {
        var file = new Argument<FileInfo>("file") { Description = "An assembly source, or an ELF executable." };
        var budget = new Option<ulong>("--max-cycles")
        {
            Description = "Give up on a configuration after this many cycles, in case it never ends.",
            DefaultValueFactory = _ => DefaultBudget,
        };
        var switches = new PipelineOptions(everyWay: true);

        var command = new Command("compare", "Run a program on the pipeline built every way, and tabulate the cycles.")
        {
            file, budget,
        };
        switches.AddTo(command);

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
            var program = ProgramFile.Load(parse.GetValue(file)!, stderr, out var exitCode);
            if (program is null)
            {
                return exitCode;
            }

            var limit = parse.GetValue(budget);
            var rows = switches.ReadEvery(parse).Select(config => Comparison.Run(program, config, limit)).ToList();
            stdout.Write(CompareTable.Write(program, rows, EnglishMessages.Instance));

            // With its hazard handling off the pipeline is meant to go wrong. Built any other
            // way, a difference from the reference machine is a fault in this program.
            if (rows.Any(row => row.Config.IsCorrect && !row.IsRight))
            {
                stderr.WriteLine("fetchline: the pipeline differed from the reference machine, which is a bug in Fetchline");
                return FetchlineCommand.Failed;
            }

            // How the program itself ended is the same in every correct configuration, so the
            // first of them speaks for all. A table of nothing but "off" has no such row, and
            // what its runs did is in the table.
            switch (rows.FirstOrDefault(row => row.Config.IsCorrect))
            {
                case { Stopped: StopReason.Fault } faulted:
                    stderr.WriteLine($"fetchline: the program stopped: {faulted.Last?.Message}");
                    return FetchlineCommand.Failed;
                case { Stopped: StopReason.Breakpoint } paused:
                    stderr.WriteLine($"fetchline: paused at an ebreak at pc 0x{paused.Last?.Pc:x8}");
                    return FetchlineCommand.Ok;
                case { Stopped: StopReason.None }:
                    stderr.WriteLine($"fetchline: still running after {limit} cycles; --max-cycles raises the limit");
                    return FetchlineCommand.Failed;
                default:
                    return FetchlineCommand.Ok;
            }
        });

        return command;
    }
}
