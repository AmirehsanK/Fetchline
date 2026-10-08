using System.CommandLine;
using Fetchline.Core.Elf;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;

namespace Fetchline.Cli.Commands;

/// <summary>
/// <c>fetchline test</c>: run a folder of official test programs and say which pass. It exits
/// non-zero when a test fails, which is what CI calls it for.
/// </summary>
internal static class TestCommand
{
    private const ulong DefaultBudget = 5_000_000;

    public static Command Build()
    {
        var directory = new Argument<DirectoryInfo>("directory") { Description = "A folder of ELF test programs." };
        var exclusions = new Option<FileInfo?>("--exclusions")
        {
            Description = "A file of tests that are expected to fail: a name and a reason on each line.",
        };
        var budget = new Option<ulong>("--max-instructions")
        {
            Description = "Give up on a test after this many instructions.",
            DefaultValueFactory = _ => DefaultBudget,
        };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "List the tests that pass as well." };
        var pipeline = new Option<bool>("--pipeline")
        {
            Description = "Run on the pipeline, checked against the reference machine instruction by instruction.",
        };
        var switches = new PipelineOptions();

        var command = new Command("test", "Run a folder of official test programs.")
        {
            directory, exclusions, budget, verbose, pipeline,
        };
        switches.AddTo(command);

        // A switch says how the pipeline is built. Without --pipeline it would change nothing,
        // and a run that quietly ignored it would look like a run that had tested it.
        command.Validators.Add(result =>
        {
            if (!result.GetValue(pipeline) && switches.AnyGiven(result))
            {
                result.AddError("The switches say how the pipeline is built; add --pipeline to run the tests on it.");
            }
        });

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
            var config = parse.GetValue(pipeline) ? switches.Read(parse) : null;
            var folder = parse.GetValue(directory)!;
            if (!folder.Exists)
            {
                stderr.WriteLine($"fetchline: there is no folder '{folder}'");
                return FetchlineCommand.Unusable;
            }

            var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parse.GetValue(exclusions) is { } list)
            {
                if (!list.Exists)
                {
                    stderr.WriteLine($"fetchline: cannot read '{list}'");
                    return FetchlineCommand.Unusable;
                }

                excluded = ParseExclusions(File.ReadAllText(list.FullName));
            }

            var files = folder.GetFiles().OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
            int passed = 0, failed = 0, skipped = 0;

            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file.FullName);
                if (!ElfFile.LooksLikeElf(bytes))
                {
                    continue;
                }

                var verdict = Run(bytes, parse.GetValue(budget), config);
                var isExcluded = excluded.Remove(file.Name, out var reason);

                // An excluded test is still run. If it passes, the list is out of date, and that
                // is a failure: otherwise a test could sit there unneeded for ever.
                if (isExcluded && !verdict.Passed)
                {
                    skipped++;
                    stdout.WriteLine($"{file.Name,-28} excluded: {reason}");
                }
                else if (isExcluded)
                {
                    failed++;
                    stdout.WriteLine($"{file.Name,-28} FAIL: passes, but is listed as excluded");
                }
                else if (verdict.Passed)
                {
                    passed++;
                    if (parse.GetValue(verbose))
                    {
                        stdout.WriteLine($"{file.Name,-28} pass");
                    }
                }
                else
                {
                    failed++;
                    stdout.WriteLine($"{file.Name,-28} FAIL: {verdict.Summary}");
                }
            }

            foreach (var missing in excluded.Keys.Order(StringComparer.Ordinal))
            {
                failed++;
                stdout.WriteLine($"{missing,-28} FAIL: listed as excluded, but there is no such test");
            }

            if (passed + failed + skipped == 0)
            {
                stderr.WriteLine($"fetchline: there are no ELF files in '{folder}'");
                return FetchlineCommand.Unusable;
            }

            var built = config is null ? string.Empty : PipelineOptions.Describe(config);
            var machine = config is null ? "reference machine"
                : built.Length == 0 ? "pipeline, in lockstep with the reference machine"
                : $"pipeline {built}, in lockstep with the reference machine";
            stdout.WriteLine($"{passed} passed, {failed} failed, {skipped} excluded ({machine})");
            return failed == 0 ? FetchlineCommand.Ok : FetchlineCommand.Failed;
        });

        return command;
    }

    /// <summary>Reads a list of tests that are expected to fail: a name, then why, on each line.</summary>
    public static Dictionary<string, string> ParseExclusions(string text)
    {
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            excluded[parts[0]] = parts.Length == 2 ? parts[1].Trim() : "no reason given";
        }

        return excluded;
    }

    /// <param name="pipeline">How the pipeline is built, or null to run on the reference machine alone.</param>
    private static TestVerdict Run(byte[] elf, ulong budget, PipelineConfig? pipeline)
    {
        if (!ElfFile.TryRead(elf, out var program, out var error))
        {
            return new TestVerdict(false, error!);
        }

        // The tests are bare-metal programs whether or not their symbols survived.
        if (pipeline is null)
        {
            var machine = new ReferenceMachine(program!, TextWriter.Null, ExecutionEnvironment.Bare);
            return TestVerdict.Of(machine.Run(budget));
        }

        var lockstep = new Lockstep(program!, TextWriter.Null, ExecutionEnvironment.Bare, pipeline);
        lockstep.Pipeline.Recording = false;

        // The budget is counted here and not read from the machine: a test may write the
        // counters, and rv32mi-p-instret_overflow sets one to nearly its largest value. A cycle
        // is not an instruction, and a slow multiplier can make it a small part of one.
        var limit = budget * (ulong)(4 + pipeline.MulDivCycles);
        CycleRecord? last = null;
        for (ulong cycle = 0; cycle < limit && !lockstep.Pipeline.IsFinished && lockstep.Divergence is null; cycle++)
        {
            last = lockstep.Step();
        }

        // A test that fails the same way on both machines has still failed; a test on which the
        // machines differ has failed whatever it wrote to tohost.
        return lockstep.Divergence is { } divergence
            ? new TestVerdict(false, "the machines disagree at " + divergence.Describe())
            : TestVerdict.Of((last?.End ?? last?.Commit).GetValueOrDefault());
    }
}
