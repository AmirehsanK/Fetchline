using System.CommandLine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;

namespace Fetchline.Cli.Commands;

/// <summary>
/// <c>fetchline trace</c>: run a program on the pipeline and print the staircase diagram, the
/// hazard log and the totals.
/// </summary>
internal static class TraceCommand
{
    private const ulong DefaultBudget = 200_000;

    public static Command Build()
    {
        var file = new Argument<FileInfo>("file") { Description = "An assembly source, or an ELF executable." };
        var from = new Option<ulong>("--from") { Description = "The first cycle to draw.", DefaultValueFactory = _ => 1 };
        var cycles = new Option<ulong>("--cycles")
        {
            Description = "How many cycles to draw; 0 draws them all.",
            DefaultValueFactory = _ => 40,
        };
        var noLog = new Option<bool>("--no-log") { Description = "Leave out the list of stalls, forwards and flushes." };
        var budget = new Option<ulong>("--max-cycles")
        {
            Description = "Stop after this many cycles, in case the program never ends.",
            DefaultValueFactory = _ => DefaultBudget,
        };

        var command = new Command("trace", "Run a program on the pipeline and draw what each cycle did.")
        {
            file, from, cycles, noLog, budget,
        };

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
            var program = ProgramFile.Load(parse.GetValue(file)!, stderr, out var exitCode);
            if (program is null)
            {
                return exitCode;
            }

            // What the program prints is not part of the picture; "fetchline run" shows it.
            var machine = new PipelineMachine(program, TextWriter.Null);
            var records = new List<CycleRecord>();
            var limit = parse.GetValue(budget);
            while (!machine.IsFinished && machine.Stopped != StopReason.Breakpoint && (ulong)records.Count < limit)
            {
                records.Add(machine.Step());
            }

            var options = new TraceOptions
            {
                From = parse.GetValue(from),
                Cycles = parse.GetValue(cycles),
                Log = !parse.GetValue(noLog),
            };
            stdout.Write(AsciiTrace.Write(program, records, EnglishMessages.Instance, options));

            var last = records.Count > 0 ? records[^1] : null;
            var stop = last?.End ?? last?.Commit;
            switch (machine.Stopped)
            {
                case StopReason.Fault:
                    stderr.WriteLine($"fetchline: the program stopped: {stop?.Message}");
                    return FetchlineCommand.Failed;
                case StopReason.Breakpoint:
                    stderr.WriteLine($"fetchline: paused at an ebreak at pc 0x{stop?.Pc:x8}");
                    return FetchlineCommand.Ok;
                case StopReason.None:
                    stderr.WriteLine($"fetchline: still running after {records.Count} cycles; --max-cycles raises the limit");
                    return FetchlineCommand.Failed;
                default:
                    return FetchlineCommand.Ok;
            }
        });

        return command;
    }
}
