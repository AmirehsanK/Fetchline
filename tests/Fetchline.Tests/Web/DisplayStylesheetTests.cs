using System.Globalization;
using System.Text.RegularExpressions;

namespace Fetchline.Tests.Web;

/// <summary>
/// The rules that keep the old-computer look usable are rules about one stylesheet, so they are
/// checked by reading it: every phosphor is readable, the plain switch turns every effect off,
/// and a reader who asks for less motion gets none.
/// </summary>
public partial class DisplayStylesheetTests
{
    private static readonly string Root = Repo.PathOf("src", "Fetchline.Web", "wwwroot");

    private static readonly string Css = File.ReadAllText(Path.Combine(Root, "css", "display.css"));

    private static readonly string[] Phosphors = ["green", "amber", "white"];

    /// <summary>The declarations of the first rule whose selector ends with <paramref name="selector"/>.</summary>
    private static string Rule(string selector)
    {
        var match = Regex.Match(Css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"there is no rule for '{selector}'");
        return match.Groups["body"].Value;
    }

    private static string Value(string declarations, string property)
    {
        var match = Regex.Match(declarations, Regex.Escape(property) + @"\s*:\s*(?<value>[^;]+);");
        Assert.True(match.Success, $"'{property}' is not set");
        return match.Groups["value"].Value.Trim();
    }

    private static (int R, int G, int B) Colour(string hex)
    {
        Assert.Matches("^#[0-9a-f]{6}$", hex);
        var value = int.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (value >> 16, (value >> 8) & 0xFF, value & 0xFF);
    }

    /// <summary>Relative luminance, as WCAG 2 defines it.</summary>
    private static double Luminance((int R, int G, int B) colour)
    {
        static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));
    }

    private static double Contrast(string one, string other)
    {
        var (a, b) = (Luminance(Colour(one)), Luminance(Colour(other)));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Fact]
    public void TheContrastFormulaIsTheStandardOne()
    {
        // The two ends of the scale, and a pair with a published ratio: #767676 on white is the
        // darkest grey that passes AA, at 4.54 to 1.
        Assert.Equal(21.0, Contrast("#000000", "#ffffff"), precision: 6);
        Assert.Equal(1.0, Contrast("#52e36b", "#52e36b"), precision: 6);
        Assert.Equal(4.54, Contrast("#767676", "#ffffff"), precision: 2);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("amber")]
    [InlineData("white")]
    public void EveryIntensityOfAPhosphorIsReadableOnItsUnlitGlass(string phosphor)
    {
        var colours = Rule($":root[data-phosphor=\"{phosphor}\"]");
        var unlit = Value(colours, "--unlit");

        // Normal, dim and bright text must all pass AA for body text: 4.5 to 1. Inverse video is
        // the same two colours the other way round, so it passes with them.
        foreach (var intensity in new[] { "--ink", "--dim", "--bright" })
        {
            var ratio = Contrast(Value(colours, intensity), unlit);
            Assert.True(ratio >= 4.5, $"{phosphor} {intensity} is {ratio:0.00} to 1 against the unlit glass");
        }

        // And they are three intensities, in order.
        Assert.True(Contrast(Value(colours, "--dim"), unlit) < Contrast(Value(colours, "--ink"), unlit));
        Assert.True(Contrast(Value(colours, "--ink"), unlit) < Contrast(Value(colours, "--bright"), unlit));
    }

    [Theory]
    [InlineData("green")]
    [InlineData("amber")]
    [InlineData("white")]
    public void TheHaloOfAPhosphorIsCloseToTheColourOfItsText(string phosphor)
    {
        var colours = Rule($":root[data-phosphor=\"{phosphor}\"]");
        var ink = Colour(Value(colours, "--ink"));
        var halo = Value(colours, "--halo").Split(' ').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();

        // The glow is the text's own light spread out, so it cannot be another colour.
        Assert.Equal(3, halo.Length);
        Assert.InRange(Math.Abs(halo[0] - ink.R) + Math.Abs(halo[1] - ink.G) + Math.Abs(halo[2] - ink.B), 0, 48);
    }

    [Fact]
    public void ThePlainSwitchTurnsEveryEffectOff()
    {
        var plain = Rule(":root[data-plain]");
        Assert.Equal("0", Value(plain, "--scanline"));
        Assert.Equal("0px", Value(plain, "--glow"));

        // The scanlines and the glass are not drawn at all, and nothing is animated: no flicker,
        // no blinking.
        Assert.Contains("display: none", Rule(":root[data-plain] .tube::after"));
        Assert.Contains("animation: none !important", Rule(":root[data-plain] *::after"));
        Assert.Contains("text-shadow: none", Rule(":root[data-plain] .inverse"));
    }

    [Fact]
    public void AReaderWhoAsksForLessMotionGetsNone()
    {
        var match = ReducedMotion().Match(Css);

        Assert.True(match.Success, "there is no rule for prefers-reduced-motion");
        Assert.Contains("animation: none !important", match.Groups["body"].Value);
        Assert.Contains("*::before", match.Groups["body"].Value);
    }

    [GeneratedRegex(@"@media \(prefers-reduced-motion: reduce\)\s*\{(?<body>.*?\})\s*\}", RegexOptions.Singleline)]
    private static partial Regex ReducedMotion();

    [Fact]
    public void EveryAnimationInTheStylesheetIsOneOfTheTwoThatAreSwitchedOff()
    {
        // A new effect must be a named animation, which the two rules above then cover.
        var names = Keyframes().Matches(Css).Select(match => match.Groups["name"].Value).Order().ToList();

        Assert.Equal(["blink", "flicker"], names);
        Assert.DoesNotContain("transition:", Css);
    }

    [GeneratedRegex(@"@keyframes (?<name>[a-z-]+)")]
    private static partial Regex Keyframes();

    [Fact]
    public void TheSwitchesOnThePageAreThePhosphorsTheStylesheetHas()
    {
        var page = File.ReadAllText(Path.Combine(Root, "index.html"));
        var script = File.ReadAllText(Path.Combine(Root, "js", "display-settings.js"));

        var keys = Regex.Matches(page, "class=\"key\" data-phosphor=\"(?<name>[a-z]+)\"").Select(match => match.Groups["name"].Value);
        Assert.Equal(Phosphors, keys);
        Assert.Contains("var phosphors = ['green', 'amber', 'white'];", script);
        Assert.All(Phosphors, phosphor => Assert.Contains($":root[data-phosphor=\"{phosphor}\"]", Css));

        // The page starts on the phosphor the specification names as the default.
        Assert.Contains("<html lang=\"en\" data-phosphor=\"green\">", page);
        Assert.Contains("data-plain aria-pressed=\"false\"", page);
    }

    [Fact]
    public void TheTubeUsesOnlyFacesThatAreAlreadyOnTheReadersMachine()
    {
        // A bundled font needs the owner's yes. Until then nothing is fetched: no @font-face,
        // no stylesheet or script from another site.
        var page = File.ReadAllText(Path.Combine(Root, "index.html"));

        Assert.DoesNotContain("@font-face", Css);
        Assert.DoesNotContain("@import", Css);
        Assert.DoesNotContain("url(", Css);
        Assert.DoesNotContain("http://", page);
        Assert.DoesNotContain("https://", page);
        Assert.Contains("monospace", Value(Rule(":root"), "--mono"));
    }
}
