using System.CommandLine;
using Fetchline.Core.Asm;
using Fetchline.Core.Elf;

namespace Fetchline.Cli.Commands;

/// <summary><c>fetchline asm</c>: assemble a source file, and report, list or save the result.</summary>
internal static class AsmCommand
{
    public static Command Build()
    {
        var file = new Argument<FileInfo>("file") { Description = "The assembly source to assemble." };
        var output = new Option<FileInfo?>("--output", "-o") { Description = "Write the program as an ELF executable." };
        var listing = new Option<bool>("--listing", "-l") { Description = "Print each instruction's address, word and text." };

        var command = new Command("asm", "Assemble a source file and report its errors, or what it became.")
        {
            file, output, listing,
        };

        command.SetAction(parse =>
        {
            var (stdout, stderr) = (parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error);
            var source = parse.GetValue(file)!;

            var program = ProgramFile.Load(source, stderr, out var exitCode);
            if (program is null)
            {
                return exitCode;
            }

            if (parse.GetValue(output) is { } target)
            {
                try
                {
                    File.WriteAllBytes(target.FullName, ElfFile.Write(program));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"fetchline: cannot write '{target}': {exception.Message}");
                    return FetchlineCommand.Unusable;
                }
            }

            if (parse.GetValue(listing))
            {
                stdout.Write(Listing.Write(program));
            }
            else
            {
                var code = program.Text?.Size ?? 0;
                var data = program.Segments.Where(segment => !segment.IsExecutable).Sum(segment => (long)segment.Size);
                var instructions = program.SourceMap.Entries.Count;
                stdout.WriteLine(
                    $"{source}: {instructions} instruction{(instructions == 1 ? "" : "s")}, " +
                    $"{code} bytes of code, {data} bytes of data, entry 0x{program.Entry:x8}");
            }

            return FetchlineCommand.Ok;
        });

        return command;
    }
}
