using System.Reflection;

namespace Fetchline.Web;

/// <summary>
/// The example programs, which are the files under <c>examples/</c> built into the assembly.
/// The tests assemble and run every one of them, so what the playground offers is known to work.
/// </summary>
internal static class Examples
{
    private const string Prefix = "Examples.";

    private static readonly Assembly Home = typeof(Examples).Assembly;

    /// <summary>The names of the examples, such as <c>load-use.s</c>, in alphabetical order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        .. Home.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The source of an example.</summary>
    public static string Read(string name)
    {
        using var stream = Home.GetManifestResourceStream(Prefix + name)
            ?? throw new ArgumentException($"There is no example called '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
