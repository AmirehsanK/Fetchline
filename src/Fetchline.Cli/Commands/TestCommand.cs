using System.CommandLine;
using Fetchline.Core.Elf;
using Fetchline.Core.Machine;
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

        var command = new Command("test", "Run a folder of official test programs.")
        {
            directory, exclusions, budget, verbose,
        };

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
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

                var verdict = Run(bytes, parse.GetValue(budget));
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

            stdout.WriteLine($"{passed} passed, {failed} failed, {skipped} excluded");
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

    private static TestVerdict Run(byte[] elf, ulong budget)
    {
        if (!ElfFile.TryRead(elf, out var program, out var error))
        {
            return new TestVerdict(false, error!);
        }

        // The tests are bare-metal programs whether or not their symbols survived.
        var machine = new ReferenceMachine(program!, TextWriter.Null, ExecutionEnvironment.Bare);
        return TestVerdict.Of(machine.Run(budget));
    }
}
