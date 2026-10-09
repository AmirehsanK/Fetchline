namespace Fetchline.Viz.Explain;

/// <summary>The languages the visualizer speaks, by the codes a page gives its <c>lang</c>.</summary>
public static class Languages
{
    public const string English = "en";

    public const string Persian = "fa";

    /// <summary>Every code there is a catalog for, the default first.</summary>
    public static IReadOnlyList<string> All { get; } = [English, Persian];

    /// <summary>The catalog for a code; English for one there is none for.</summary>
    public static IMessages For(string? code) => code == Persian ? PersianMessages.Instance : EnglishMessages.Instance;

    /// <summary>Whether the language is written from right to left.</summary>
    public static bool IsRightToLeft(string? code) => code == Persian;
}
