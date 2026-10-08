using Fetchline.Core.Asm;

namespace Fetchline.Tests.Asm;

public class DiagnosticTests
{
    [Fact]
    public void ADiagnosticRendersWithItsLineACaretAndAHint()
    {
        const string source = "main:\n    addi a0, a8, 1\n    ret\n";
        var span = new SourceSpan(source.IndexOf("a8", StringComparison.Ordinal), 2, 2, 14);
        var diagnostic = new Diagnostic(Severity.Error, "unknown register 'a8'", span, "did you mean 'a7'?");

        Assert.Equal(
            "prog.s:2:14: error: unknown register 'a8'\n" +
            "      addi a0, a8, 1\n" +
            "               ^~\n" +
            "  did you mean 'a7'?\n",
            diagnostic.Render(source, "prog.s"));
    }

    [Fact]
    public void ATabBeforeTheCaretStaysATab()
    {
        const string source = "\tfoo a0";
        var diagnostic = new Diagnostic(Severity.Warning, "odd", new SourceSpan(1, 3, 1, 2));

        Assert.Equal("1:2: warning: odd\n  \tfoo a0\n  \t^~~\n", diagnostic.Render(source));
    }

    [Fact]
    public void ASpanPastTheEndOfTheSourceStillRenders()
    {
        var diagnostic = new Diagnostic(Severity.Error, "expected an operand", new SourceSpan(3, 0, 1, 4));

        Assert.Equal("1:4: error: expected an operand\n  add\n     ^\n", diagnostic.Render("add"));
    }

    [Fact]
    public void TheBagRemembersWhetherAnythingWasAnError()
    {
        var bag = new DiagnosticBag();
        bag.Warning(default, "careful");
        Assert.False(bag.HasErrors);

        bag.Error(default, "broken");
        Assert.True(bag.HasErrors);
        Assert.Equal(2, bag.Items.Count);
    }

    [Theory]
    [InlineData("adid", "addi")]
    [InlineData("addi", "addi")]
    [InlineData("ADDI", "addi")]
    [InlineData("jall", "jalr")]     // a wrong letter is likelier than an extra one
    [InlineData("a8", "a7")]
    [InlineData("mulhsv", "mulhsu")]
    [InlineData("mluhus", "mulhsu")]
    [InlineData("stpr", null)]      // two edits from "sp": too many for so short a name
    [InlineData("xyz", null)]
    public void SuggestFindsTheLikelySpelling(string typed, string? expected)
    {
        string[] candidates = ["add", "addi", "and", "andi", "jal", "jalr", "mulhsu", "sp", "a7", "s7"];

        Assert.Equal(expected, Suggest.Closest(typed, candidates));
    }

    [Fact]
    public void EditDistanceCountsASwapAsOneEdit()
    {
        Assert.Equal(0, Suggest.Distance("addi", "addi"));
        Assert.Equal(1, Suggest.Distance("adid", "addi"));
        Assert.Equal(1, Suggest.Distance("add", "addi"));
        Assert.Equal(1, Suggest.Distance("xddi", "addi"));
        Assert.Equal(4, Suggest.Distance("", "addi"));
        Assert.Equal("did you mean 'jal'?", Suggest.Hint("jsl", ["jal", "jalr"]));
        Assert.Null(Suggest.Hint("zzzz", ["jal", "jalr"]));
    }
}
