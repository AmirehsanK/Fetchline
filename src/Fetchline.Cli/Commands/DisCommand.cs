using System.CommandLine;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;

namespace Fetchline.Cli.Commands;

/// <summary><c>fetchline dis</c>: print the code of an ELF file or of a source file as a listing.</summary>
internal static class DisCommand
{
    public static Command Build()
    {
        var file = new Argument<FileInfo>("file") { Description = "An ELF executable, or an assembly source." };
        var noAliases = new Option<bool>("--no-aliases")
        {
            Description = "Show the real instruction behind each alias: 'addi a0, zero, 5' for 'li a0, 5'.",
        };
        var numeric = new Option<bool>("--numeric") { Description = "Write registers as x0 to x31." };

        var command = new Command("dis", "Disassemble the code of a program.") { file, noAliases, numeric };

        command.SetAction(parse =>
        {
            var configuration = parse.InvocationConfiguration;
            var program = ProgramFile.Load(parse.GetValue(file)!, configuration.Error, out var exitCode);
            if (program is null)
            {
                return exitCode;
            }

            var options = new DisassemblyOptions
            {
                Aliases = !parse.GetValue(noAliases),
                Registers = parse.GetValue(numeric) ? RegisterStyle.Numeric : RegisterStyle.Abi,
            };
            configuration.Output.Write(Listing.Write(program, options));
            return FetchlineCommand.Ok;
        });

        return command;
    }
}
