using System.Reflection;

namespace Fetchline.Viz;

/// <summary>One example program.</summary>
/// <param name="Name">Its file name, such as <c>load-use.s</c>.</param>
/// <param name="Summary">What it is, in one sentence: the first sentence of the comment it opens with.</param>
public sealed record Example(string Name, string Summary, string Source);

/// <summary>
/// The example programs, which are the files under <c>examples/</c> built into this assembly, so
/// that what the playground offers is what the tests assemble and run. They are listed in the
/// order a reader might take them: first the four that each show one thing a pipeline does, then
/// whole programs from the shortest to the longest.
/// </summary>
public static class ExampleCatalog
{
    private const string Prefix = "Examples.";

    private static readonly string[] Order =
    [
        "load-use.s", "forwarding.s", "branch.s", "multiply.s",
        "hello.s", "sum.s", "fib.s", "gcd.s", "factorial.s", "bubble-sort.s", "primes.s",
    ];

    /// <summary>Every example, in the order of the menu. One that has no place in that order comes last.</summary>
    public static IReadOnlyList<Example> All { get; } = Load();

    /// <summary>The example a visit starts with.</summary>
    public static Example First => All[0];

    /// <summary>The example of that name, or null.</summary>
    public static Example? Find(string name) => All.FirstOrDefault(example => example.Name == name);

    /// <summary>
    /// The first sentence of the comment a source opens with, without its full stop: what the
    /// program says it is. Empty when it does not open with a comment.
    /// </summary>
    public static string SummaryOf(string source)
    {
        var words = new List<string>();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('#'))
            {
                break;
            }

            // A blank comment line ends the paragraph the summary is taken from.
            var text = line.TrimStart('#').Trim();
            if (text.Length == 0)
            {
                break;
            }

            words.Add(text);
        }

        var paragraph = string.Join(' ', words);
        var end = paragraph.IndexOf(". ", StringComparison.Ordinal);
        return (end < 0 ? paragraph : paragraph[..end]).TrimEnd('.');
    }

    private static List<Example> Load()
    {
        var home = typeof(ExampleCatalog).Assembly;
        return
        [
            .. home.GetManifestResourceNames()
                .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(name => Read(home, name))
                .OrderBy(example => Array.IndexOf(Order, example.Name) is var place and >= 0 ? place : int.MaxValue)
                .ThenBy(example => example.Name, StringComparer.Ordinal),
        ];
    }

    private static Example Read(Assembly home, string resource)
    {
        using var stream = home.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd().ReplaceLineEndings("\n");
        return new Example(resource[Prefix.Length..], SummaryOf(source), source);
    }
}
