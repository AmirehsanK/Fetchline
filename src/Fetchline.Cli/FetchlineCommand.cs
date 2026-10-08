using System.CommandLine;
using Fetchline.Cli.Commands;

namespace Fetchline.Cli;

/// <summary>
/// The command line. Each subcommand is a thin layer over the engine: it reads files, calls
/// <c>Fetchline.Core</c>, and writes text. Commands write to the invocation's output, never to
/// <see cref="Console"/> directly, so a test can run one in-process and compare what it printed.
/// </summary>
internal static class FetchlineCommand
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The input was read but is wrong: the source has errors, a test failed.</summary>
    public const int Failed = 1;

    /// <summary>The command could not be carried out: a file is missing or unreadable.</summary>
    public const int Unusable = 2;

    public static RootCommand Build() =>
        new("Fetchline: a RISC-V RV32IM assembler, emulator and five-stage pipeline tracer.")
        {
            AsmCommand.Build(),
            DisCommand.Build(),
            RunCommand.Build(),
            TestCommand.Build(),
            TraceCommand.Build(),
        };
}
