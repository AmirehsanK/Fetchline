using System.Globalization;
using System.Text;

namespace Fetchline.Core.Asm;

/// <summary>How serious a diagnostic is. Any error means no program is produced.</summary>
public enum Severity : byte
{
    Warning,
    Error,
}

/// <summary>A stretch of one source line. Lines and columns count from one.</summary>
/// <param name="Start">The offset of the first character from the start of the source.</param>
public readonly record struct SourceSpan(int Start, int Length, int Line, int Column)
{
    /// <summary>The span from the start of this one to the end of <paramref name="last"/>.</summary>
    public SourceSpan Through(SourceSpan last) =>
        last.Start + last.Length <= Start ? this : this with { Length = last.Start + last.Length - Start };
}

/// <summary>
/// Something the assembler has to say about the source. Diagnostics are collected, never thrown,
/// so one run reports every mistake it can find, and each one points at a line and a column.
/// </summary>
/// <param name="Hint">A suggestion, such as "did you mean 'addi'?". Shown on its own line.</param>
public sealed record Diagnostic(Severity Severity, string Message, SourceSpan Span, string? Hint = null)
{
    /// <summary>
    /// The diagnostic as a compiler would print it: location, message, the source line, a caret
    /// under the offending text, and the hint.
    /// </summary>
    public string Render(string source, string? fileName = null)
    {
        var text = new StringBuilder();
        if (fileName is not null)
        {
            text.Append(fileName).Append(':');
        }

        text.Append(CultureInfo.InvariantCulture, $"{Span.Line}:{Span.Column}: ");
        text.Append(Severity == Severity.Error ? "error: " : "warning: ").Append(Message).Append('\n');

        var line = LineAt(source, Span.Start);
        if (line.Length > 0)
        {
            text.Append("  ").Append(line).Append('\n').Append("  ");

            // Whitespace is copied from the line itself so that a tab before the caret stays a tab.
            var column = Math.Min(Span.Column - 1, line.Length);
            foreach (var c in line[..column])
            {
                text.Append(c == '\t' ? '\t' : ' ');
            }

            var width = Math.Clamp(Span.Length, 1, Math.Max(1, line.Length - column));
            text.Append('^').Append('~', width - 1).Append('\n');
        }

        if (Hint is not null)
        {
            text.Append("  ").Append(Hint).Append('\n');
        }

        return text.ToString();
    }

    private static string LineAt(string source, int offset)
    {
        offset = Math.Clamp(offset, 0, source.Length);
        var start = offset == 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;
        var end = source.IndexOf('\n', offset);
        if (end < 0)
        {
            end = source.Length;
        }

        return source[start..end].TrimEnd('\r');
    }
}

/// <summary>The diagnostics of one assembly, in the order they were found.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors { get; private set; }

    public void Error(SourceSpan span, string message, string? hint = null)
    {
        _items.Add(new Diagnostic(Severity.Error, message, span, hint));
        HasErrors = true;
    }

    public void Warning(SourceSpan span, string message, string? hint = null) =>
        _items.Add(new Diagnostic(Severity.Warning, message, span, hint));
}
