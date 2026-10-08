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

    /// <summary>Every name a switch can have, in the order of its values.</summary>
    public static string[] All<T>(Func<T, string> nameOf)
        where T : struct, Enum => [.. Enum.GetValues<T>().Select(nameOf)];

    /// <summary>The value a name stands for. The name must be one of <see cref="All{T}"/>.</summary>
    public static T Parse<T>(string name, Func<T, string> nameOf)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (nameOf(value) == name)
            {
                return value;
            }
        }

        throw new ArgumentException($"'{name}' is not one of: {string.Join(", ", All(nameOf))}.");
    }
}
