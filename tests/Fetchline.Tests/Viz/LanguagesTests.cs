using System.Reflection;
using System.Text.RegularExpressions;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;

namespace Fetchline.Tests.Viz;

/// <summary>
/// The two languages. That each has every message is the compiler's business; what is checked
/// here is that a message says something, keeps everything it was given to say, and that the
/// Persian one is in Persian wherever it is a sentence.
/// </summary>
public partial class LanguagesTests
{
    // Members whose text is a technical term, a formula, or made only of what it was given.
    private static readonly HashSet<string> NotSentences =
    [
        nameof(IMessages.BufferHeading), nameof(IMessages.CpiHeading), nameof(IMessages.ColumnHeading),
        nameof(IMessages.WireCarries), nameof(IMessages.WireAsserted), nameof(IMessages.WrongWith),
        nameof(IMessages.Problem), nameof(IMessages.Guesses),
    ];

    [GeneratedRegex(@"[؀-ۿ]")]
    private static partial Regex PersianLetter();

    /// <summary>Every message of a catalog: its name, the text arguments it was given, and what it said.</summary>
    private static IEnumerable<(string Name, string[] Given, string Said)> Everything(IMessages messages)
    {
        foreach (var property in typeof(IMessages).GetProperties())
        {
            yield return (property.Name, [], (string)property.GetValue(messages)!);
        }

        foreach (var method in typeof(IMessages).GetMethods().Where(method => !method.IsSpecialName))
        {
            // An enum is tried at each of its values; everything else is given one sample.
            var parameters = method.GetParameters();
            var choices = parameters.FirstOrDefault(parameter => parameter.ParameterType.IsEnum) is { } choice
                ? Enum.GetValues(choice.ParameterType).Cast<object>().ToArray()
                : [null!];
            foreach (var value in choices)
            {
                var arguments = parameters.Select((parameter, index) => Sample(parameter, index, value)).ToArray();
                var said = (string)method.Invoke(messages, arguments)!;
                yield return (method.Name, [.. arguments.OfType<string>()], said);
            }
        }
    }

    private static object Sample(ParameterInfo parameter, int index, object? chosen)
    {
        var type = parameter.ParameterType;
        return type.IsEnum ? chosen!
            : type == typeof(string) ? $"<{parameter.Name}{index}>"
            : type == typeof(int) ? (object)2
            : type == typeof(ulong) ? (object)3ul
            : type == typeof(double) ? (object)1.5
            : throw new InvalidOperationException($"no sample for a {type.Name}");
    }

    [Fact]
    public void ThereIsACatalogForEachLanguageAndEnglishForAnyOther()
    {
        Assert.Equal(["en", "fa"], Languages.All);
        Assert.IsType<EnglishMessages>(Languages.For("en"));
        Assert.IsType<PersianMessages>(Languages.For("fa"));
        Assert.IsType<EnglishMessages>(Languages.For("xx"));
        Assert.IsType<EnglishMessages>(Languages.For(null));
        Assert.True(Languages.IsRightToLeft("fa") && !Languages.IsRightToLeft("en"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fa")]
    public void EveryMessageSaysSomethingAndLeavesOutNothingItWasGiven(string language)
    {
        var all = Everything(Languages.For(language)).ToList();
        Assert.True(all.Count > 150, "the catalog has shrunk, or the test no longer finds it");

        foreach (var (name, given, said) in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(said), $"{language}: {name} says nothing");
            foreach (var argument in given)
            {
                Assert.True(said.Contains(argument, StringComparison.Ordinal), $"{language}: {name} drops {argument}: '{said}'");
            }
        }
    }

    [Fact]
    public void ThePersianCatalogIsInPersianAndTheEnglishOneIsNot()
    {
        foreach (var (name, _, said) in Everything(PersianMessages.Instance))
        {
            // A wire that carries a register's number is named as the datapath names it.
            var term = NotSentences.Contains(name) || (name == nameof(IMessages.NameOf) && !PersianLetter().IsMatch(said) && said.Length <= 6);
            Assert.True(term || PersianLetter().IsMatch(said), $"{name} is not in Persian: '{said}'");
        }

        Assert.All(Everything(EnglishMessages.Instance), message => Assert.DoesNotMatch(PersianLetter(), message.Said));
    }

    [Fact]
    public void ThePersianLogSaysWhatTheEnglishOneSays()
    {
        var session = new Session(File.ReadAllText(Repo.PathOf("examples", "load-use.s")));
        session.Seek(ulong.MaxValue);
        var labels = new InstructionLabels(session.Program!);
        var records = session.Recorded(1, session.Frontier);

        var english = HazardLog.Build(records, labels, EnglishMessages.Instance, null);
        var persian = HazardLog.Build(records, labels, PersianMessages.Instance, null);

        Assert.Equal(english.Select(line => (line.Cycle, line.Kind)), persian.Select(line => (line.Cycle, line.Kind)));
        Assert.Equal("load-use: add (ID) needs x4; lw (EX) has it only after MEM", english[0].Text);
        Assert.Equal("بار و مصرف: add در ID به x4 نیاز دارد؛ lw در EX آن را تازه پس از MEM دارد", persian[0].Text);
        Assert.Equal("MEM/WB -> EX.A   x4 از lw", persian[1].Text);
    }

    [Fact]
    public void TheComparisonTableKeepsItsColumnsInPersian()
    {
        var program = AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "sum.s")));
        var rows = Comparison.Configurations(PipelineConfig.Default).Select(config => Comparison.Run(program, config, 100_000)).ToList();
        var english = CompareTable.Lines(program, rows, EnglishMessages.Instance);
        var persian = CompareTable.Lines(program, rows, PersianMessages.Instance);

        // The headings are the terms of the command line, and a row is made of characters that
        // are each one cell wide, so the columns stand under their headings.
        Assert.Equal(english[0].Text, persian[0].Text);
        var width = persian[0].Text.Length;
        foreach (var line in persian.Where(line => line.Row is { IsRight: true, Ended: true }))
        {
            Assert.True(line.Text.All(char.IsAscii), line.Text);
            Assert.Equal(width, line.Text.Length);
        }

        // What is said under the table is a sentence, and is in Persian.
        Assert.Matches(PersianLetter(), persian[^1].Text);
    }

    [Fact]
    public void ThePageHasASwitchForEachLanguageAndKeepsItsDiagramsTheRightWayRound()
    {
        var root = Repo.PathOf("src", "Fetchline.Web", "wwwroot");
        var page = File.ReadAllText(Path.Combine(root, "index.html"));
        var css = File.ReadAllText(Path.Combine(root, "css", "display.css"));
        var settings = File.ReadAllText(Path.Combine(root, "js", "display-settings.js"));

        Assert.Equal(Languages.All, DataLanguage().Matches(page).Select(match => match.Groups[1].Value));
        Assert.Contains("var languages = ['en', 'fa'];", settings);
        Assert.Contains("var rightToLeft = ['fa'];", settings);

        // In a page that runs from the right, these still run from the left.
        var kept = LeftToRight().Match(css);
        Assert.True(kept.Success, "there is no rule that keeps the diagrams left to right");
        foreach (var part in new[] { ".staircase", ".datapath", ".editor", ".register-grid", ".dump", ".compare", ".timeline input" })
        {
            Assert.Contains(part, kept.Groups[1].Value);
        }

        Assert.Contains("font-family: var(--mono)", kept.Groups[2].Value);
    }

    [GeneratedRegex("data-language=\"([a-z]+)\"")]
    private static partial Regex DataLanguage();

    [GeneratedRegex(@":root\[dir=""rtl""\] :is\(([^)]*)\),\s*:root\[dir=""rtl""\] \.chin \{([^}]*direction: ltr;[^}]*)\}")]
    private static partial Regex LeftToRight();
}
