using System.Globalization;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz;

/// <summary>
/// The values of the what-if switches by their short names. The command line takes these names
/// and the comparison table prints them, so a row of the table can be typed back as options.
/// They are not translated: they are what is typed, like a mnemonic.
/// </summary>
public static class SwitchNames
{
    public static string Of(HazardHandling hazards) => hazards switch
    {
        HazardHandling.StallOnly => "stall",
        HazardHandling.Off => "off",
        _ => "forwarding",
    };

    public static string Of(BranchDecision branches) => branches == BranchDecision.Decode ? "id" : "ex";

    public static string Of(Predictor predictor) => predictor switch
    {
        Predictor.BackwardTaken => "backward-taken",
        Predictor.OneBit => "1-bit",
        Predictor.TwoBit => "2-bit",
        _ => "not-taken",
    };

    /// <summary>The three switches a comparison varies, in the order its table lists them.</summary>
    public static string Of(PipelineConfig config) =>
        $"{Of(config.Hazards)}, {Of(config.Branches)}, {Of(config.Predictor)}";

    /// <summary>The word for no cache.</summary>
    public const string NoCache = "off";

    /// <summary>The miss penalty a cache has when its description does not say.</summary>
    public const int DefaultMissPenalty = 10;

    /// <summary>
    /// A cache as it is typed: <c>off</c>, or sets, ways and bytes in a block with the miss
    /// penalty after a colon, as in <c>16x2x16:10</c>.
    /// </summary>
    public static string Of(CacheConfig? cache) => cache is null
        ? NoCache
        : string.Create(CultureInfo.InvariantCulture, $"{cache.Sets}x{cache.Ways}x{cache.BlockBytes}:{cache.MissPenalty}");

    /// <summary>
    /// Reads a cache as it is typed. The penalty may be left out. False for text that is not a
    /// cache, and for a cache that cannot be built, with the reason.
    /// </summary>
    public static bool TryParseCache(string text, out CacheConfig? cache, out string? problem)
    {
        (cache, problem) = (null, null);
        if (text == NoCache)
        {
            return true;
        }

        var parts = text.Split(':');
        var shape = parts[0].Split('x');
        var numbers = new int[4];
        numbers[3] = DefaultMissPenalty;
        var read = parts.Length <= 2 && shape.Length == 3;
        for (var i = 0; read && i < 3; i++)
        {
            read = Number(shape[i], out numbers[i]);
        }

        if (read && parts.Length == 2)
        {
            read = Number(parts[1], out numbers[3]);
        }

        if (!read)
        {
            problem = $"'{text}' is not a cache: it is '{NoCache}', or sets, ways and bytes in a block as in 16x2x16, with the miss penalty after a colon if it is not {DefaultMissPenalty}.";
            return false;
        }

        var candidate = new CacheConfig(numbers[0], numbers[1], numbers[2], numbers[3]);
        try
        {
            candidate.Validate();
        }
        catch (ArgumentException refused)
        {
            problem = refused.Message;
            return false;
        }

        cache = candidate;
        return true;

        // Digits only: no sign, no spaces, nothing a link could smuggle in.
        static bool Number(string digits, out int number)
        {
            number = 0;
            return digits.Length is > 0 and <= 5 && digits.All(char.IsAsciiDigit)
                && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
    }

    /// <summary>Every name a switch can have, in the order of its values.</summary>
    public static string[] All<T>(Func<T, string> nameOf)
        where T : struct, Enum => [.. Enum.GetValues<T>().Select(nameOf)];

    /// <summary>The value a name stands for. The name must be one of <see cref="All{T}"/>.</summary>
    public static T Parse<T>(string name, Func<T, string> nameOf)
        where T : struct, Enum =>
        TryParse(name, nameOf, out var value)
            ? value
            : throw new ArgumentException($"'{name}' is not one of: {string.Join(", ", All(nameOf))}.");

    /// <summary>The value a name stands for, if it stands for one: for names that come from outside.</summary>
    public static bool TryParse<T>(string name, Func<T, string> nameOf, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (nameOf(candidate) == name)
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
