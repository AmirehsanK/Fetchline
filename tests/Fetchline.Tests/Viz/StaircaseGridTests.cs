using Fetchline.Core.Pipeline;
using Fetchline.Viz;
using Fetchline.Viz.Explain;
using Fetchline.Viz.Export;
using Fetchline.Viz.Staircase;

namespace Fetchline.Tests.Viz;

/// <summary>
/// The pipeline diagram as a grid of characters, with the forwards drawn into it. It is what the
/// playground shows, and without its arrows it is what <c>fetchline trace</c> prints.
/// </summary>
public class StaircaseGridTests
{
    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    /// <summary>Runs a program to its end and draws the cycles asked for; all of them by default.</summary>
    private static StaircaseGrid Grid(string source, PipelineConfig? config = null, ulong from = 1, ulong to = 0, bool arrows = true)
    {
        var session = new Session(source, config);
        session.Seek(ulong.MaxValue);
        var last = to == 0 ? session.Cycle : to;
        return StaircaseGrid.Build(session.Records(1, session.Cycle), new InstructionLabels(session.Program!), from, last, arrows);
    }

    private static GridCell Cell(StaircaseGrid grid, int row, ulong cycle) => grid.Rows[row - 1].Cells[(int)(cycle - grid.From)];

    [Fact]
    public void TheLoadUseSequenceIsTheTextbookDiagramWithItsTwoForwardsDrawnIn()
    {
        var grid = Grid(Example("load-use.s"));

        // The lw's value leaves MEM at the end of cycle 4 and enters the add's EX in cycle 5;
        // the add's result leaves EX at the end of cycle 5 and enters the sub's EX in cycle 6.
        Assert.Equal(
            """
                                      1    2    3    4    5    6    7    8
             0x00 lw   x4, 0(x2)      IF   ID   EX   MEM─┐WB
             0x04 add  x5, x4, x6          IF   ID   ID  └EX──┐MEM  WB
             0x08 sub  x7, x5, x4               IF   IF   ID  └EX   MEM  WB

            """.ReplaceLineEndings("\n"),
            grid.ToText());

        Assert.Equal([new GridArrow(5, Producer: 1, Consumer: 2), new GridArrow(6, Producer: 2, Consumer: 3)], grid.Arrows);
        Assert.Equal((1ul, 8ul, 8, 5, 26), (grid.From, grid.To, grid.Columns, grid.CellWidth, grid.LabelWidth));
        Assert.Equal((true, false), (grid.HasHeld, grid.HasSquashed));
    }

    [Fact]
    public void ACellSaysWhatBecameOfTheInstructionInThatCycle()
    {
        var grid = Grid(Example("load-use.s"));

        // The add waits in ID in cycle 3 and goes on in cycle 4; the sub waits behind it in IF.
        Assert.Equal(new GridCell("ID", CellKind.Held, "   "), Cell(grid, 2, 3));
        Assert.Equal(new GridCell("ID", CellKind.Normal, "  └"), Cell(grid, 2, 4));
        Assert.Equal(new GridCell("IF", CellKind.Held, "   "), Cell(grid, 3, 3));
        Assert.Equal(new GridCell("MEM", CellKind.Normal, "─┐"), Cell(grid, 1, 4));
        Assert.Equal(new GridCell(string.Empty, CellKind.Empty, "     "), Cell(grid, 3, 1));

        // Every cell is as wide as every other, whatever is in it.
        Assert.All(grid.Rows.SelectMany(row => row.Cells), cell => Assert.Equal(5, cell.Text.Length + cell.Tail.Length));
        Assert.All(grid.Rows, row => Assert.Equal((26, 8), (row.Label.Length, row.Cells.Count)));
        Assert.Equal(["1    ", "2    ", "8    "], new[] { 0, 1, 7 }.Select(grid.Heading));
    }

    [Fact]
    public void WithoutItsArrowsTheGridIsTheDiagramTheTerminalPrints()
    {
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var session = new Session(File.ReadAllText(path));
            session.Seek(300);
            var records = session.Records(1, session.Cycle);
            var labels = new InstructionLabels(session.Program!);

            foreach (var (from, to) in new[] { (1ul, session.Cycle), (1ul, Math.Min(session.Cycle, 12ul)), (Math.Min(session.Cycle, 7ul), session.Cycle) })
            {
                var plain = StaircaseGrid.Build(records, labels, from, to, arrows: false);
                var drawn = StaircaseGrid.Build(records, labels, from, to);

                // The terminal's trace begins with exactly this text.
                var trace = AsciiTrace.Write(
                    session.Program!, records, EnglishMessages.Instance, new TraceOptions { From = from, Cycles = to - from + 1 });
                Assert.StartsWith(plain.ToText(), trace);
                Assert.Empty(plain.Arrows);

                // And the arrows are drawn only where the plain grid has spaces: taking them
                // out again gives the plain grid back.
                var erased = string.Concat(drawn.ToText().Select(c => "─┐┤└├│┼".Contains(c) ? ' ' : c));
                Assert.Equal(
                    plain.ToText().Split('\n').Select(line => line.TrimEnd()),
                    erased.Split('\n').Select(line => line.TrimEnd()));
            }
        }
    }

    [Fact]
    public void TwoOperandsForwardedAtOnceComeDownOneLine()
    {
        var grid = Grid("li t0, 3\nli t1, 4\nadd a0, t0, t1");

        // In cycle 5 the add takes t0 from MEM/WB and t1 from EX/MEM. The first li's line comes
        // down past the second li, which joins it, and the two arrive together.
        Assert.Equal(new GridCell("MEM", CellKind.Normal, "─┐"), Cell(grid, 1, 4));
        Assert.Equal(new GridCell("EX", CellKind.Normal, "──┤"), Cell(grid, 2, 4));
        Assert.Equal(new GridCell("ID", CellKind.Normal, "  └"), Cell(grid, 3, 4));
        Assert.Equal(2, grid.Arrows.Count);
    }

    [Fact]
    public void AnArrowPassesTheInstructionsBetweenItsEnds()
    {
        var grid = Grid("li t0, 3\nnop\naddi a0, t0, 1");

        // The addi is two behind the li, so the value comes from MEM/WB, past the nop.
        Assert.Equal(new GridCell("MEM", CellKind.Normal, "─┐"), Cell(grid, 1, 4));
        Assert.Equal(new GridCell("EX", CellKind.Normal, "  │"), Cell(grid, 2, 4));
        Assert.Equal(new GridCell("ID", CellKind.Normal, "  └"), Cell(grid, 3, 4));
    }

    [Fact]
    public void ABranchDecidedInDecodeHasItsOperandBroughtToDecode()
    {
        var inDecode = new PipelineConfig { Branches = BranchDecision.Decode };
        var grid = Grid("addi t0, zero, 1\nbnez t0, over\nnop\nover:\nnop", inDecode);

        // The branch waits in ID in cycle 3 for the addi in EX, and in cycle 4 takes the result
        // from EX/MEM: the arrow enters its second ID cell, not an EX cell.
        Assert.Equal(new GridCell("EX", CellKind.Normal, "──┐"), Cell(grid, 1, 3));
        Assert.Equal(new GridCell("ID", CellKind.Held, "  └"), Cell(grid, 2, 3));
        Assert.Equal(new GridCell("ID", CellKind.Normal, "   "), Cell(grid, 2, 4));
        Assert.Equal([new GridArrow(4, Producer: 1, Consumer: 2)], grid.Arrows);
    }

    [Fact]
    public void OneResultCanGoToTwoInstructionsInTheSameCycle()
    {
        var inDecode = new PipelineConfig { Branches = BranchDecision.Decode };
        var grid = Grid("addi t0, zero, 1\nadd a0, t0, t0\nbeqz t0, over\nnop\nover:\nnop", inDecode);

        // In cycle 4 the addi's result is in EX/MEM. The add in EX takes it for both operands,
        // and the branch in ID takes it for its comparator. One line serves both: it reaches the
        // add and goes on down to the branch.
        Assert.Equal(new GridCell("EX", CellKind.Normal, "──┐"), Cell(grid, 1, 3));
        Assert.Equal(new GridCell("ID", CellKind.Normal, "  ├"), Cell(grid, 2, 3));
        Assert.Equal(new GridCell("IF", CellKind.Normal, "  └"), Cell(grid, 3, 3));
        Assert.Equal(
            [new GridArrow(4, 1, 2), new GridArrow(4, 1, 2), new GridArrow(4, 1, 3)],
            grid.Arrows.OrderBy(arrow => arrow.Consumer));
    }

    [Fact]
    public void ASquashedInstructionIsMarkedInItsLastCellAndInTheOneAfter()
    {
        var grid = Grid("j over\nli a0, 1\nli a1, 2\nover:\nnop");

        // The jump is decided in EX in cycle 3; what is in ID and IF then is thrown away.
        Assert.Equal(new GridCell("ID", CellKind.Squashed, "   "), Cell(grid, 2, 3));
        Assert.Equal(new GridCell("--", CellKind.Gone, "   "), Cell(grid, 2, 4));
        Assert.Equal(new GridCell("IF", CellKind.Squashed, "   "), Cell(grid, 3, 3));
        Assert.Equal(new GridCell("--", CellKind.Gone, "   "), Cell(grid, 3, 4));
        Assert.Equal((false, true), (grid.HasHeld, grid.HasSquashed));
    }

    [Fact]
    public void AWindowShowsOnlyItsCyclesAndTheArrowsThatBeginInThem()
    {
        var whole = Grid(Example("load-use.s"));
        var late = Grid(Example("load-use.s"), from: 5, to: 8);

        // The forward into cycle 5 began in cycle 4, which is not shown; the one into cycle 6 is.
        Assert.Equal(
            """
                                      5    6    7    8
             0x00 lw   x4, 0(x2)      WB
             0x04 add  x5, x4, x6     EX──┐MEM  WB
             0x08 sub  x7, x5, x4     ID  └EX   MEM  WB

            """.ReplaceLineEndings("\n"),
            late.ToText());
        Assert.Equal([new GridArrow(6, 2, 3)], late.Arrows);
        Assert.Equal(whole.LabelWidth, late.LabelWidth);

        // An instruction that had left the pipeline before the window has no row in it.
        var later = Grid(Example("load-use.s"), from: 7, to: 8);
        Assert.Equal([2ul, 3ul], later.Rows.Select(row => row.Seq));

        // A window with nothing in it is an empty grid, not a mistake.
        var none = Grid(Example("load-use.s"), from: 20, to: 25);
        Assert.Empty(none.Rows);
        Assert.Equal(0, Grid(Example("load-use.s"), from: 9, to: 8).Columns);
    }

    [Fact]
    public void TheLabelsAreAsWideAsTheWidestInstructionInTheProgramWhateverIsShown()
    {
        // The long instruction is the last, and is not in the first few cycles; the columns are
        // where they would be if it were, so stepping never shifts them.
        const string source = "nop\nnop\nnop\nnop\nnop\nnop\ncsrrwi t0, mscratch, 21\n";
        var early = Grid(source, to: 4);
        var whole = Grid(source);

        Assert.Equal(whole.LabelWidth, early.LabelWidth);
        Assert.Equal(" 0x00 nop".PadRight(whole.LabelWidth), early.Rows[0].Label);
        Assert.Equal(" 0x18 csrrwi t0, mscratch, 21".PadRight(whole.LabelWidth), whole.Rows[^1].Label);
        Assert.Equal(" 0x18 csrrwi t0, mscratch, 21".Length + 5, whole.LabelWidth);
    }

    [Fact]
    public void TheCyclesShownAreTheOnesThatFitEndingWithTheOneOnScreen()
    {
        // Labels of 26 characters, as the load-use example has, and cells of five.
        var widths = new InstructionLabels(new Session(Example("load-use.s")).Program!).Widths;
        Assert.Equal(26, StaircaseGrid.LabelWidthFor(widths, compact: false));
        Assert.Equal(7, StaircaseGrid.LabelWidthFor(widths, compact: true));

        // A hundred characters: 74 left for cycles, fourteen of them, the last being the 40th.
        Assert.Equal(new GridWindow(27, 40, Compact: false), StaircaseGrid.Fit(widths, current: 40, characters: 100));

        // Early in a run there are not yet that many cycles to show.
        Assert.Equal(new GridWindow(1, 5, Compact: false), StaircaseGrid.Fit(widths, 5, 100));

        // Room for exactly eight cycles keeps the full labels; a character less gives them up,
        // and the space they took goes to cycles instead.
        Assert.Equal(new GridWindow(33, 40, Compact: false), StaircaseGrid.Fit(widths, 40, 26 + 40));
        Assert.Equal(new GridWindow(30, 40, Compact: true), StaircaseGrid.Fit(widths, 40, 26 + 39));

        // A phone: forty characters, short labels, six cycles. And never fewer than four.
        Assert.Equal(new GridWindow(35, 40, Compact: true), StaircaseGrid.Fit(widths, 40, 40));
        Assert.Equal(new GridWindow(37, 40, Compact: true), StaircaseGrid.Fit(widths, 40, 12));
        Assert.Equal(new GridWindow(37, 40, Compact: true), StaircaseGrid.Fit(widths, 40, 0));

        // Nothing earlier than the oldest record is asked for.
        Assert.Equal(new GridWindow(36, 40, Compact: false), StaircaseGrid.Fit(widths, 40, 100, firstKept: 36));

        // Past cycle 9,999 a cell is six wide, so fewer fit: 74 / 6 = 12.
        Assert.Equal(new GridWindow(19_989, 20_000, Compact: false), StaircaseGrid.Fit(widths, 20_000, 100));
    }

    [Fact]
    public void OnANarrowScreenAnInstructionIsLabelledByItsMnemonicAlone()
    {
        var session = new Session(Example("load-use.s"));
        session.Seek(ulong.MaxValue);
        var labels = new InstructionLabels(session.Program!);
        var window = StaircaseGrid.Fit(labels.Widths, session.Cycle, characters: 40);
        var grid = StaircaseGrid.Build(session.Records(window.From, window.To), labels, window);

        Assert.Equal(
            """
                   3    4    5    6    7    8
             lw    EX   MEM─┐WB
             add   ID   ID  └EX──┐MEM  WB
             sub   IF   IF   ID  └EX   MEM  WB

            """.ReplaceLineEndings("\n"),
            grid.ToText());
        Assert.Equal((7, 6), (grid.LabelWidth, grid.Columns));
    }

    [Fact]
    public void CyclesWithManyDigitsGetWiderCells()
    {
        var session = new Session("spin: j spin");
        session.Seek(10_003);
        var grid = StaircaseGrid.Build(
            session.Records(9_990, session.Cycle), new InstructionLabels(session.Program!), 9_998, 10_003);

        Assert.Equal(6, grid.CellWidth);
        Assert.Equal(["9998  ", "9999  ", "10000 "], new[] { 0, 1, 2 }.Select(grid.Heading));
        Assert.All(grid.Rows.SelectMany(row => row.Cells), cell => Assert.Equal(6, cell.Text.Length + cell.Tail.Length));

        // " 0x00 j    spin" and the gap: twenty characters before the first cycle.
        Assert.Equal(20, grid.LabelWidth);
        Assert.StartsWith(new string(' ', 20) + "9998  9999  10000 10001 10002 10003\n", grid.ToText());
    }

    [Fact]
    public void TheDrawnForwardsAreExactlyTheForwardsThatHappened()
    {
        // Every forward event whose producer is still in view is an arrow, and nothing else is.
        foreach (var example in new[] { "bubble-sort.s", "factorial.s", "fib.s", "gcd.s" })
        {
            var session = new Session(Example(example));
            session.Seek(400);
            var records = session.Records(1, session.Cycle);
            var grid = StaircaseGrid.Build(records, new InstructionLabels(session.Program!), 1, session.Cycle);

            var happened = records
                .Where(record => record.Cycle > 1)
                .SelectMany(record => record.Events.OfType<ForwardEvent>()
                    .Select(forward => new GridArrow(record.Cycle, forward.Producer, forward.Seq)))
                .ToList();
            Assert.Equal(happened, grid.Arrows);
            Assert.NotEmpty(happened);

            // An arrow's two ends are a row apart at least, and its producer is the older.
            Assert.All(grid.Arrows, arrow => Assert.True(arrow.Producer < arrow.Consumer));
        }
    }
}
