using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;
using Fetchline.Viz;
using Fetchline.Viz.Editor;

namespace Fetchline.Tests.Viz;

/// <summary>The source as the editor draws it: cut up by the assembler's own lexer.</summary>
public class SourceViewTests
{
    private static SourceView View(string source) => SourceView.Of(Assembler.Assemble(source));

    /// <summary>The pieces of a line that are one kind of thing, as text.</summary>
    private static string[] Pieces(SourceView view, int line, Ink ink) =>
        [.. view.Lines[line - 1].Runs.Where(run => run.Ink == ink).Select(run => view.Text(run.Start, run.Length))];

    /// <summary>A line as its kinds: "Label Punctuation Mnemonic ...", spaces left out.</summary>
    private static string Kinds(SourceView view, int line) => string.Join(
        ' ',
        view.Lines[line - 1].Runs
            .Where(run => view.Text(run.Start, run.Length).Trim().Length > 0)
            .Select(run => run.Ink));

    [Fact]
    public void EveryKindOfThingInAProgramIsToldApart()
    {
        var view = View("""
            main:   li   a0, 5          # five
                    .word 0x10, 'a'
            1:      bnez a0, 1b
            msg:    .ascii "hi there"
                    lui  t1, %hi(msg)
                    sw   a0, 4(sp)
            size = 8
                    csrw mscratch, x5
            """);

        Assert.Empty(view.Diagnostics);
        Assert.Equal("Label Punctuation Mnemonic Register Punctuation Number Comment", Kinds(view, 1));
        Assert.Equal(["main"], Pieces(view, 1, Ink.Label));
        Assert.Equal(["# five"], Pieces(view, 1, Ink.Comment));

        Assert.Equal("Directive Number Punctuation Number", Kinds(view, 2));
        Assert.Equal(["0x10", "'a'"], Pieces(view, 2, Ink.Number));

        // A numeric local label is a label where it is defined and a symbol where it is used.
        Assert.Equal("Label Punctuation Mnemonic Register Punctuation Symbol", Kinds(view, 3));
        Assert.Equal(["1"], Pieces(view, 3, Ink.Label));
        Assert.Equal(["1b"], Pieces(view, 3, Ink.Symbol));

        Assert.Equal("Label Punctuation Directive Text", Kinds(view, 4));
        Assert.Equal(["\"hi there\""], Pieces(view, 4, Ink.Text));

        Assert.Equal("Mnemonic Register Punctuation Directive Punctuation Symbol Punctuation", Kinds(view, 5));
        Assert.Equal(["%hi"], Pieces(view, 5, Ink.Directive));

        Assert.Equal("Mnemonic Register Punctuation Number Punctuation Register Punctuation", Kinds(view, 6));
        Assert.Equal("Label Punctuation Number", Kinds(view, 7));
        Assert.Equal("Mnemonic Symbol Punctuation Register", Kinds(view, 8));
    }

    [Fact]
    public void TheRunsOfALineAreTheLineAndNothingElse()
    {
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var source = File.ReadAllText(path);
            var view = View(source);
            var lines = source.ReplaceLineEndings("\n").Split('\n');

            Assert.Equal(lines.Length, view.Lines.Count);
            foreach (var line in view.Lines)
            {
                Assert.Equal(lines[line.Number - 1], string.Concat(line.Runs.Select(run => view.Text(run.Start, run.Length))));
                Assert.Equal(lines[line.Number - 1], view.Text(line.Start, line.Length));

                // In order, touching, none empty, and no two neighbours alike.
                var at = line.Start;
                SourceRun? before = null;
                foreach (var run in line.Runs)
                {
                    Assert.Equal(at, run.Start);
                    Assert.True(run.Length > 0);
                    Assert.False(before is { } previous && previous.Ink == run.Ink && previous.Flagged == run.Flagged);
                    (at, before) = (run.Start + run.Length, run);
                }
            }

            // A program that assembles has no word the lexer or the editor is puzzled by.
            Assert.Empty(view.Diagnostics);
            Assert.DoesNotContain(
                view.Lines.SelectMany(line => line.Runs),
                run => run.Ink == Ink.Wrong || run.Flagged || (run.Ink == Ink.Plain && view.Text(run.Start, run.Length).Trim().Length > 0));
        }
    }

    [Fact]
    public void ACommentOrAStringOverSeveralLinesIsThatOnEachOfThem()
    {
        var view = View("li a0, 1  /* one\n   two\n   three */ li a1, 2\n");

        Assert.Equal(["/* one"], Pieces(view, 1, Ink.Comment));
        Assert.Equal(["   two"], Pieces(view, 2, Ink.Comment));
        Assert.Equal(["   three */"], Pieces(view, 3, Ink.Comment));

        // The statement after the comment is still the one begun before it: "li" there is not
        // a mnemonic but a stray name, and the assembler says so.
        Assert.Equal("Mnemonic Register Punctuation Number Comment", Kinds(view, 1));
    }

    [Fact]
    public void MnemonicsAreKnownInEitherCaseAndUnknownOnesAreLeftPlain()
    {
        var view = View("LI a0, 5\nadid a0, a0, 1\nnop\n");

        Assert.Equal(["LI"], Pieces(view, 1, Ink.Mnemonic));
        Assert.Equal(["adid"], Pieces(view, 2, Ink.Plain).Where(text => text.Trim().Length > 0));
        Assert.Equal(["nop"], Pieces(view, 3, Ink.Mnemonic));

        // The unknown one is where the assembler points, and its line is marked.
        var flagged = Assert.Single(view.Lines.SelectMany(line => line.Runs), run => run.Flagged);
        Assert.Equal("adid", view.Text(flagged.Start, flagged.Length));
        Assert.Equal([false, true, false, false], view.Lines.Select(line => line.HasProblem));
        Assert.Equal("unknown instruction 'adid'", Assert.Single(view.Diagnostics).Message);
    }

    [Fact]
    public void AComplaintAboutSomethingMissingStillMarksSomething()
    {
        // Each of these is wrong at a place with no text of its own: the end of a line.
        foreach (var source in new[] { "li a0,\n", "addi a0, a0\n", "lw a0, 4(sp\n", ".word\n" })
        {
            var view = View(source);

            Assert.NotEmpty(view.Diagnostics);
            Assert.True(view.Lines[0].HasProblem, source);
            Assert.Contains(view.Lines[0].Runs, run => run.Flagged);
        }
    }

    [Fact]
    public void TextTheLexerCannotUseIsMarkedAsWrong()
    {
        var view = View("li a0, 5 @ 3\n");

        Assert.Equal(["@"], Pieces(view, 1, Ink.Wrong));
        Assert.True(view.Lines[0].HasProblem);
    }

    [Fact]
    public void ALineHasTheAddressOfItsFirstInstruction()
    {
        var view = View("""
            .data
            cell: .word 7
            .text
            main:
                li   a0, 5          # one instruction
                la   t0, cell       # two
                lw   a1, 0(t0)
            done: nop
            """);

        Assert.Equal(
            [null, null, null, null, 0u, 4u, 12u, 16u],
            view.Lines.Select(line => line.Address));
        Assert.Equal(4, view.AddressDigits);
    }

    [Fact]
    public void TheGutterShowsWhichStageEachLineIsIn()
    {
        var session = new Session(File.ReadAllText(Repo.PathOf("examples", "load-use.s")));
        var view = SourceView.Of(session.Assembly);
        var first = view.Lines.First(line => line.Address == 0).Number;

        Assert.Empty(view.Stages(session.Record));          // at reset nothing is anywhere

        // Cycle 3: the lw is in EX, the add is held in ID behind it, the sub is held in IF.
        session.Seek(3);
        var stages = view.Stages(session.Record);
        Assert.Equal(3, stages.Count);
        Assert.Equal([new StageMark(Stage.Execute, Occupancy.Normal)], stages[first]);
        Assert.Equal([new StageMark(Stage.Decode, Occupancy.Held)], stages[first + 1]);
        Assert.Equal([new StageMark(Stage.Fetch, Occupancy.Held)], stages[first + 2]);

        // Cycle 8: only the sub is left, in WB.
        session.Seek(8);
        Assert.Equal([new StageMark(Stage.WriteBack, Occupancy.Normal)], Assert.Single(view.Stages(session.Record)).Value);
    }

    [Fact]
    public void ALineThatBecameTwoInstructionsCanBeInTwoStages()
    {
        var session = new Session(".data\ncell: .word 7\n.text\nla t0, cell\nnop\n");
        var view = SourceView.Of(session.Assembly);

        // Cycle 3: the auipc half of the la is in EX and the addi half in ID, both from line 4.
        session.Seek(3);
        Assert.Equal(
            [new StageMark(Stage.Decode, Occupancy.Normal), new StageMark(Stage.Execute, Occupancy.Normal)],
            view.Stages(session.Record)[4]);
    }

    [Fact]
    public void ASquashedInstructionIsMarkedAsSuch()
    {
        var session = new Session("j over\nli a0, 1\nli a1, 2\nover:\nnop\n");
        var view = SourceView.Of(session.Assembly);

        // Cycle 3: the jump is decided in EX and the two instructions behind it are thrown away.
        session.Seek(3);
        var stages = view.Stages(session.Record);
        Assert.Equal([new StageMark(Stage.Decode, Occupancy.Squashed)], stages[2]);
        Assert.Equal([new StageMark(Stage.Fetch, Occupancy.Squashed)], stages[3]);
    }

    [Fact]
    public void AFinalLineBreakLeavesAnEmptyLastLineAndCarriageReturnsAreNotText()
    {
        var view = View("nop\r\nli a0, 1\r\n");

        Assert.Equal(3, view.Lines.Count);
        Assert.Equal(["nop", "li a0, 1", string.Empty], view.Lines.Select(line => view.Text(line.Start, line.Length)));
        Assert.Empty(view.Lines[2].Runs);
        Assert.Equal([0u, 4u, null], view.Lines.Select(line => line.Address));

        Assert.Single(View(string.Empty).Lines);
        Assert.Equal(2, View("\n").Lines.Count);
    }

    [Fact]
    public void AHalfTypedProgramIsCutUpLikeAnyOther()
    {
        // No program comes out of this, but the editor still has every line to draw.
        var session = new Session("main:\n    li a0, \n    add a0, a0, \"unclosed\n");
        var view = SourceView.Of(session.Assembly);

        Assert.Null(session.Program);
        Assert.Equal(4, view.Lines.Count);
        Assert.All(view.Lines, line => Assert.Null(line.Address));
        Assert.Equal(["li"], Pieces(view, 2, Ink.Mnemonic));
        Assert.Equal(["main"], Pieces(view, 1, Ink.Label));
        Assert.Empty(view.Stages(session.Record));
        Assert.NotEmpty(view.Diagnostics);
    }
}
