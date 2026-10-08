using System.CommandLine;
using Fetchline.Cli;

namespace Fetchline.Tests.Cli;

/// <summary>Runs the command line in-process and captures what it printed.</summary>
internal static class CommandLine
{
    public static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        var configuration = new InvocationConfiguration { Output = output, Error = error };

        var exitCode = FetchlineCommand.Build().Parse(arguments).Invoke(configuration);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs a command that must succeed and print nothing on the error stream.</summary>
    public static string RunOk(params string[] arguments)
    {
        var (exitCode, output, error) = Run(arguments);
        Assert.True(exitCode == 0 && error.Length == 0, $"exit code {exitCode}\n{error}");
        return output;
    }

    /// <summary>A file in the examples folder.</summary>
    public static string Example(string name) => Repo.PathOf("examples", name);

    /// <summary>A source written to a temporary file that is deleted when disposed.</summary>
    public static TemporaryFile Source(string text) => new(text, ".s");
}

internal sealed class TemporaryFile : IDisposable
{
    public TemporaryFile(string? text = null, string extension = "")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fetchline-" + Guid.NewGuid().ToString("n") + extension);
        if (text is not null)
        {
            File.WriteAllText(Path, text);
        }
    }

    public string Path { get; }

    public void Dispose() => File.Delete(Path);
}
