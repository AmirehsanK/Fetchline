namespace Fetchline.Cli;

// An explicit entry point instead of top-level statements: those would declare a class named
// Program in the global namespace, which would hide the engine's Program type in this project.
internal static class EntryPoint
{
    private static int Main(string[] args) => FetchlineCommand.Build().Parse(args).Invoke();
}
