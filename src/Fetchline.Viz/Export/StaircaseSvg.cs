using System.Globalization;
using System.Text;
using Fetchline.Viz.Staircase;

namespace Fetchline.Viz.Export;

/// <summary>The four colours a picture of the diagram is drawn in: a phosphor.</summary>
/// <param name="Unlit">The glass.</param>
/// <param name="Ink">Ordinary text.</param>
/// <param name="Dim">Headings, and what was thrown away.</param>
/// <param name="Bright">The lines of the forwards.</param>
public sealed record SvgColours(string Unlit, string Ink, string Dim, string Bright)
{
    /// <summary>The green the playground starts with.</summary>
    public static SvgColours Green { get; } = new("#07120a", "#52e36b", "#2e9a48", "#c9ffd3");
}

/// <summary>
/// The pipeline diagram as a picture that stands on its own: an SVG with no stylesheet, no font
/// and no script from anywhere. It is the grid the terminal prints and the playground draws,
/// marked the same way: a held stage in inverse video, a squashed one struck out, and a forward
/// as a line from the stage that had the value to the stage that took it.
///
/// Every cell is placed by its own coordinates, so the columns line up whatever monospaced face
/// the reader's machine draws the text in, and the lines of the forwards are drawn, not typed.
/// </summary>
public static class StaircaseSvg
{
    // The size of one character cell. The width is that of a monospaced face at this size, near
    // enough: a cell holds at most three letters, so being a little off moves nothing visibly.
    private const double CharWidth = 8.4;
    private const double RowHeight = 20;
    private const double FontSize = 14;
    private const double Margin = 12;

    private const string Faces = "'Cascadia Mono', Consolas, 'DejaVu Sans Mono', 'Liberation Mono', Menlo, monospace";

    /// <param name="title">What the picture is of, for a reader who cannot see it.</param>
    public static string Write(StaircaseGrid grid, string title, SvgColours? colours = null)
    {
        colours ??= SvgColours.Green;
        var cell = grid.CellWidth * CharWidth;
        var left = Margin + grid.LabelWidth * CharWidth;
        var width = left + grid.Columns * cell + Margin;
        var height = Margin * 2 + (grid.Rows.Count + 1) * RowHeight;

        // The middle of the line of text in a row; row zero is the heading.
        double Middle(int row) => Margin + row * RowHeight + RowHeight / 2;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(width)} {N(height)}\" width=\"{N(width)}\" height=\"{N(height)}\" role=\"img\"");
        svg.Append(CultureInfo.InvariantCulture,
            $" font-family=\"{Faces}\" font-size=\"{N(FontSize)}\" dominant-baseline=\"central\" fill=\"{colours.Ink}\">\n");
        svg.Append("<title>").Append(Escape(title)).Append("</title>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"100%\" height=\"100%\" fill=\"{colours.Unlit}\"/>\n");

        for (var column = 0; column < grid.Columns; column++)
        {
            Text(svg, left + column * cell, Middle(0), grid.Heading(column).TrimEnd(), colours.Dim);
        }

        var rowOf = new Dictionary<ulong, int>();
        for (var i = 0; i < grid.Rows.Count; i++)
        {
            var row = grid.Rows[i];
            rowOf[row.Seq] = i;
            var y = Middle(i + 1);
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(Margin)}\" y=\"{N(y)}\" xml:space=\"preserve\">{Escape(row.Label.TrimEnd())}</text>\n");

            for (var column = 0; column < row.Cells.Count; column++)
            {
                var (text, kind) = (row.Cells[column].Text, row.Cells[column].Kind);
                var x = left + column * cell;
                switch (kind)
                {
                    case CellKind.Empty:
                        break;
                    case CellKind.Held:
                        svg.Append(CultureInfo.InvariantCulture,
                            $"<rect x=\"{N(x - 1)}\" y=\"{N(y - RowHeight / 2 + 2)}\" width=\"{N(text.Length * CharWidth + 2)}\" height=\"{N(RowHeight - 4)}\" fill=\"{colours.Ink}\"/>\n");
                        Text(svg, x, y, text, colours.Unlit);
                        break;
                    case CellKind.Squashed:
                        Text(svg, x, y, text, colours.Dim, " text-decoration=\"line-through\"");
                        break;
                    case CellKind.Gone:
                        Text(svg, x, y, text, colours.Dim);
                        break;
                    default:
                        Text(svg, x, y, text, null);
                        break;
                }
            }
        }

        // A forward taken in a cycle leaves its producer at the end of the cycle before: along
        // from the producer's stage, down the boundary between the two cycles, and into the
        // consumer's stage.
        foreach (var arrow in grid.Arrows)
        {
            if (!rowOf.TryGetValue(arrow.Producer, out var top) || !rowOf.TryGetValue(arrow.Consumer, out var bottom)
                || arrow.Cycle <= grid.From || arrow.Cycle > grid.To)
            {
                continue;
            }

            var column = (int)(arrow.Cycle - grid.From);
            var boundary = left + column * cell - CharWidth / 2;
            var from = left + (column - 1) * cell + grid.Rows[top].Cells[column - 1].Text.Length * CharWidth + 2;
            svg.Append(CultureInfo.InvariantCulture,
                $"<path d=\"M{N(from)} {N(Middle(top + 1))} H{N(boundary)} V{N(Middle(bottom + 1))} H{N(boundary + CharWidth / 2 - 1)}\" fill=\"none\" stroke=\"{colours.Bright}\" stroke-width=\"1.4\"/>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void Text(StringBuilder svg, double x, double y, string text, string? fill, string more = "")
    {
        if (text.Length == 0)
        {
            return;
        }

        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(x)}\" y=\"{N(y)}\"");
        if (fill is not null)
        {
            svg.Append(CultureInfo.InvariantCulture, $" fill=\"{fill}\"");
        }

        svg.Append(more).Append('>').Append(Escape(text)).Append("</text>\n");
    }

    private static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
