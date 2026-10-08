using System.Reflection;

namespace Fetchline.Tests;

/// <summary>Paths into the repository, for tests that read examples, golden files and vectors.</summary>
internal static class Repo
{
    /// <summary>The repository root, recorded when the test project was built.</summary>
    public static string Root { get; } = typeof(Repo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "RepositoryRoot")
        .Value!;

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
}
