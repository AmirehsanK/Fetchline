using System.Text;
using Fetchline.Core.Asm;
using Fetchline.Core.Elf;

namespace Fetchline.Cli;

/// <summary>Turns a path on the command line into a program: an ELF file is read, anything else is assembled.</summary>
internal static class ProgramFile
{
    /// <summary>
    /// Loads a program. On failure the reason has been written to <paramref name="error"/> and
    /// the result is null; <paramref name="exitCode"/> says which kind of failure it was.
    /// </summary>
    public static Program? Load(FileInfo file, TextWriter error, out int exitCode)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"fetchline: cannot read '{file}': {exception.Message}");
            exitCode = FetchlineCommand.Unusable;
            return null;
        }

        if (ElfFile.LooksLikeElf(bytes))
        {
            if (!ElfFile.TryRead(bytes, out var loaded, out var reason))
            {
                error.WriteLine($"fetchline: '{file}' is {reason}");
                exitCode = FetchlineCommand.Failed;
                return null;
            }

            exitCode = FetchlineCommand.Ok;
            return loaded;
        }

        var result = Assembler.Assemble(Encoding.UTF8.GetString(bytes));
        error.Write(result.RenderDiagnostics(file.ToString()));
        if (!result.Success)
        {
            var errors = result.Diagnostics.Count(diagnostic => diagnostic.Severity == Severity.Error);
            error.WriteLine($"fetchline: {errors} error{(errors == 1 ? "" : "s")} in '{file}'");
        }

        exitCode = result.Success ? FetchlineCommand.Ok : FetchlineCommand.Failed;
        return result.Program;
    }
}
