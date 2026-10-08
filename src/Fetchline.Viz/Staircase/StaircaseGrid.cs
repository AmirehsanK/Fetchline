using System.Globalization;
using System.Text;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Export;

namespace Fetchline.Viz.Staircase;

/// <summary>What a cell of the grid holds.</summary>
public enum CellKind : byte
{
    /// <summary>The instruction was not in the pipeline in this cycle.</summary>
    Empty,

    /// <summary>It did its work in the stage and moved on.</summary>
    Normal,

    /// <summary>It was kept in the stage for another cycle.</summary>
    Held,

    /// <summary>It was in the stage when it was thrown away.</summary>
    Squashed,

    /// <summary>The cycle after it was thrown away: the mark that says so, and nothing more.</summary>
    Gone,
}

/// <summary>
/// One cell of the grid: an instruction in one cycle. It is as wide as every other cell. The
/// text is the name of a stage, and the tail is the rest of the cell: spaces, or the part of a
/// forwarding arrow that runs through it.
/// </summary>
public readonly record struct GridCell(string Text, CellKind Kind, string Tail);

/// <summary>One row of the grid: one instruction as it was fetched, and its cell in every cycle shown.</summary>
/// <param name="Label">The address and the instruction, padded to the width of the label column.</param>
public sealed record GridRow(ulong Seq, uint Pc, string Label, IReadOnlyList<GridCell> Cells);

/// <summary>A value handed from one instruction to another: an arrow in the grid.</summary>
/// <param name="Cycle">The cycle in which the consumer took it.</param>
public readonly record struct GridArrow(ulong Cycle, ulong Producer, ulong Consumer);

/// <summary>Which cycles a grid shows, and whether its labels are the short ones.</summary>
public readonly record struct GridWindow(ulong From, ulong To, bool Compact);

/// <summary>
/// The pipeline diagram as a grid of characters: the picture <c>fetchline trace</c> prints, with
/// the forwards drawn into it. An arrow leaves the stage that produced a value at the end of
/// one cycle and enters the stage that uses it at the start of the next, so it runs down the
/// last column of the earlier cycle's cells, between the two instructions:
/// <code>
///  lw    IF   ID   EX   MEM─┐WB
///  add        IF   ID   ID  └EX   MEM  WB
/// </code>
/// </summary>
public sealed class StaircaseGrid
{
    // Which ways a line leaves the last character of a cell.
    private const int Up = 1, Down = 2, Left = 4, Right = 8;

    // The space between the longest instruction and the first cycle.
    private const int Gap = 5;

    // A diagram is never narrower than this many cycles, and gives up its full labels before
    // it would show fewer than this many.
    private const int FewestColumns = 4;
    private const int FewestWithFullLabels = 8;

    private StaircaseGrid(
        ulong from, ulong to, int cellWidth, int labelWidth, List<GridRow> rows, List<GridArrow> arrows)
    {
        From = from;
        To = to;
        CellWidth = cellWidth;
        LabelWidth = labelWidth;
        Rows = rows;
        Arrows = arrows;
    }

    /// <summary>The first and the last cycle shown.</summary>
    public ulong From { get; }

    public ulong To { get; }

    /// <summary>How many characters a cycle takes across: five, or more when the numbers need it.</summary>
    public int CellWidth { get; }

    /// <summary>How many characters the labels take, the gap before the first cycle included.</summary>
    public int LabelWidth { get; }

    /// <summary>The instructions that were in the pipeline at some time in the cycles shown, oldest first.</summary>
    public IReadOnlyList<GridRow> Rows { get; }

    /// <summary>The forwards drawn, in the order they happened.</summary>
    public IReadOnlyList<GridArrow> Arrows { get; }

    /// <summary>How many cycles are shown.</summary>
    public int Columns => To < From ? 0 : (int)(To - From + 1);

    /// <summary>Whether anything in the grid is held: for a legend.</summary>
    public bool HasHeld => Rows.Any(row => row.Cells.Any(cell => cell.Kind == CellKind.Held));

    /// <summary>Whether the mark of a squashed instruction is anywhere in the grid: for a legend.</summary>
    public bool HasSquashed => Rows.Any(row => row.Cells.Any(cell => cell.Kind == CellKind.Gone));

    /// <param name="records">The records of the cycles to draw. Others may be among them and are ignored.</param>
    /// <param name="from">The first cycle to draw.</param>
    /// <param name="to">The last cycle to draw.</param>
    /// <param name="arrows">Whether the forwards are drawn in. Without them the grid is the textbook diagram alone.</param>
    /// <param name="compact">
    /// Whether an instruction is labelled by its mnemonic alone, for a screen too narrow to
    /// spend half its width on addresses and operands.
    /// </param>
    public static StaircaseGrid Build(
        IReadOnlyList<CycleRecord> records, InstructionLabels labels, ulong from, ulong to, bool arrows = true,
        bool compact = false) =>
        Build(StaircaseLayout.Build(records), records, labels, from, to, arrows, compact);

    /// <summary>The same, for a caller that has already laid the records out.</summary>
    public static StaircaseGrid Build(
        StaircaseLayout layout, IReadOnlyList<CycleRecord> records, InstructionLabels labels, ulong from, ulong to,
        bool arrows = true, bool compact = false)
    {
        var widths = labels.Widths;
        var cellWidth = CellWidthFor(to);
        var labelWidth = LabelWidthFor(widths, compact);
        var columns = to < from ? 0 : (int)(to - from + 1);

        // A row is shown if any of its cells, or the mark of its being squashed, is in the window.
        var shown = layout.Rows
            .Where(row => row.FirstCycle <= to && row.LastCycle + (row.WasSquashed ? 1ul : 0) >= from)
            .ToList();
        var indexOf = new Dictionary<ulong, int>();
        for (var i = 0; i < shown.Count; i++)
        {
            indexOf[shown[i].Seq] = i;
        }

        // The lines of the arrows, as the ways each leaves the last character of each cell.
        var lines = new int[shown.Count, columns];
        var drawn = new List<GridArrow>();
        foreach (var record in records)
        {
            // An arrow for a forward in cycle c is drawn in the cells of cycle c - 1.
            if (!arrows || record.Cycle <= from || record.Cycle > to)
            {
                continue;
            }

            var column = (int)(record.Cycle - 1 - from);
            foreach (var forward in record.Events.OfType<ForwardEvent>())
            {
                if (!indexOf.TryGetValue(forward.Producer, out var top) || !indexOf.TryGetValue(forward.Seq, out var bottom) || top >= bottom)
                {
                    continue;
                }

                lines[top, column] |= Left | Down;
                lines[bottom, column] |= Up | Right;
                for (var between = top + 1; between < bottom; between++)
                {
                    lines[between, column] |= Up | Down;
                }

                drawn.Add(new GridArrow(record.Cycle, forward.Producer, forward.Seq));
            }
        }

        var rows = new List<GridRow>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var row = shown[i];
            var cells = new GridCell[columns];
            for (var column = 0; column < columns; column++)
            {
                var cycle = from + (ulong)column;
                var (text, kind) = row.At(cycle) is { } cell
                    ? (AsciiTrace.Name(cell.Stage), KindOf(cell.State))
                    : row.WasSquashed && cycle == row.LastCycle + 1 ? (AsciiTrace.SquashedMark, CellKind.Gone)
                    : (string.Empty, CellKind.Empty);
                cells[column] = new GridCell(text, kind, Tail(lines[i, column], cellWidth - text.Length));
            }

            rows.Add(new GridRow(row.Seq, row.Pc, Label(labels, row, widths, compact).PadRight(labelWidth), cells));
        }

        return new StaircaseGrid(from, to, cellWidth, labelWidth, rows, drawn);
    }

    /// <summary>
    /// Chooses what to draw in a space so many characters across: the cycles that fit, ending
    /// with the one on screen, so that nothing has to be scrolled to see what just happened.
    /// When the full labels would leave room for fewer than eight cycles the short ones are
    /// used, and however narrow the space is, four cycles are drawn.
    /// </summary>
    /// <param name="current">The cycle on screen: the last one drawn.</param>
    /// <param name="characters">The width available.</param>
    /// <param name="firstKept">The earliest cycle there is a record of.</param>
    public static GridWindow Fit(LabelWidths widths, ulong current, int characters, ulong firstKept = 1)
    {
        var cell = CellWidthFor(current);
        var compact = characters < LabelWidthFor(widths, compact: false) + (FewestWithFullLabels * cell);
        var columns = Math.Max(FewestColumns, (characters - LabelWidthFor(widths, compact)) / cell);
        var from = current > (ulong)columns ? current - (ulong)columns + 1 : 1;
        return new GridWindow(Math.Max(from, Math.Max(firstKept, 1)), current, compact);
    }

    /// <summary>The grid for a window chosen by <see cref="Fit"/>.</summary>
    public static StaircaseGrid Build(IReadOnlyList<CycleRecord> records, InstructionLabels labels, GridWindow window) =>
        Build(records, labels, window.From, window.To, compact: window.Compact);

    /// <summary>
    /// How many characters the labels of a program take, before any grid is built: what is
    /// needed to work out how many cycles fit beside them.
    /// </summary>
    public static int LabelWidthFor(LabelWidths widths, bool compact) => compact
        ? 1 + widths.Mnemonic + 1                               // " lw     "
        : 1 + 2 + widths.Address + 1 + widths.Text + Gap;       // " 0x00 lw   x4, 0(x2)     "

    /// <summary>The width of a cycle's cells when the last cycle shown is <paramref name="to"/>.</summary>
    public static int CellWidthFor(ulong to) => Math.Max(5, to.ToString(CultureInfo.InvariantCulture).Length + 1);

    /// <summary>The number of a column as the header shows it, padded to a cell.</summary>
    public string Heading(int column) =>
        (From + (ulong)column).ToString(CultureInfo.InvariantCulture).PadRight(CellWidth);

    /// <summary>
    /// The grid as plain text: a line of cycle numbers and a line for each instruction. Without
    /// its arrows it is, character for character, the diagram <see cref="AsciiTrace"/> writes.
    /// </summary>
    public string ToText()
    {
        var text = new StringBuilder();
        var header = new StringBuilder().Append(' ', LabelWidth);
        for (var column = 0; column < Columns; column++)
        {
            header.Append(Heading(column));
        }

        text.Append(header.ToString().TrimEnd()).Append('\n');
        foreach (var row in Rows)
        {
            var line = new StringBuilder(row.Label);
            foreach (var cell in row.Cells)
            {
                line.Append(cell.Text).Append(cell.Tail);
            }

            text.Append(line.ToString().TrimEnd()).Append('\n');
        }

        return text.ToString();
    }

    private static CellKind KindOf(Occupancy state) => state switch
    {
        Occupancy.Held => CellKind.Held,
        Occupancy.Squashed => CellKind.Squashed,
        _ => CellKind.Normal,
    };

    private static string Label(InstructionLabels labels, StaircaseRow row, LabelWidths widths, bool compact)
    {
        var text = labels.Describe(row.Pc, row.Raw);
        if (compact)
        {
            return " " + text.Mnemonic;
        }

        return " 0x" + row.Pc.ToString("x", CultureInfo.InvariantCulture).PadLeft(widths.Address, '0') + " "
            + (text.Operands.Length == 0 ? text.Mnemonic : text.Mnemonic.PadRight(widths.Mnemonic) + text.Operands);
    }

    /// <summary>
    /// The rest of a cell after its text. A line that leaves to the left starts at the text and
    /// runs along to the last character; the last character is the corner, the tee or the
    /// upright that the lines through it make.
    /// </summary>
    private static string Tail(int lines, int length)
    {
        if (lines == 0)
        {
            return new string(' ', length);
        }

        var last = lines switch
        {
            Left | Down => '┐',             // the value leaves its producer
            Left | Down | Up => '┤',        // another producer joins a line already coming down
            Up | Right => '└',              // it arrives at its consumer
            Up | Right | Down => '├',       // one consumer, and the line goes on to another
            Up | Down => '│',               // past an instruction in between
            _ => '┼',                       // every way at once
        };
        return new string((lines & Left) != 0 ? '─' : ' ', length - 1) + last;
    }
}
