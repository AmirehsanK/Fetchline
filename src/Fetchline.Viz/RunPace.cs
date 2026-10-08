namespace Fetchline.Viz;

/// <summary>
/// How fast a run goes when it is left to go on its own: so many cycles, then a pause so long,
/// and again. The slow paces are for watching one cycle follow another. The fastest does a
/// slice of many cycles between pauses, and the pause is only there to let the page be drawn
/// and the keys be heard.
/// </summary>
/// <param name="Name">What the pace is called on its key: cycles a second, or the word for flat out.</param>
/// <param name="Cycles">How many cycles are run at a time.</param>
/// <param name="Milliseconds">How long to wait before the next lot.</param>
public readonly record struct RunPace(string Name, int Cycles, int Milliseconds)
{
    /// <summary>The paces there are, slowest first. The last has no speed of its own to name.</summary>
    public static IReadOnlyList<RunPace> All { get; } =
    [
        new("1/s", 1, 1000),
        new("4/s", 1, 250),
        new("16/s", 1, 62),
        new(string.Empty, 5000, 1),
    ];

    /// <summary>The pace a run starts at: fast enough not to bore, slow enough to follow.</summary>
    public static RunPace Default => All[1];

    /// <summary>Whether this is the pace that does not wait to be watched.</summary>
    public bool IsFlatOut => Name.Length == 0;

    /// <summary>The next pace up, and after the fastest the slowest again.</summary>
    public RunPace Next()
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == this)
            {
                return All[(i + 1) % All.Count];
            }
        }

        return Default;
    }
}
