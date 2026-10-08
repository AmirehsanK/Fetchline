using System.CommandLine;

namespace Fetchline.Cli;

/// <summary>
/// The command line. Each subcommand is a thin layer over the engine: it reads files, calls
/// <c>Fetchline.Core</c>, and writes text. Commands write to the invocation's output, never to
/// <see cref="Console"/> directly, so a test can run one in-process and compare what it printed.
/// </summary>
internal static class FetchlineCommand
{
    public static RootCommand Build() =>
        new("Fetchline: a RISC-V RV32IM assembler, emulator and five-stage pipeline tracer.");
}
