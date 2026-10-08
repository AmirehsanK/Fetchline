using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.Editor;

/// <summary>What a piece of source is. The editor decides how each kind is drawn.</summary>
public enum Ink : byte
{
    /// <summary>Spaces, and anything nothing more particular can be said about.</summary>
    Plain,

    /// <summary>The name of an instruction, real or pseudo, where a statement begins.</summary>
    Mnemonic,

    /// <summary>A directive such as <c>.word</c>, or a relocation operator such as <c>%hi</c>.</summary>
    Directive,

    Register,

    /// <summary>A number or a character constant.</summary>
    Number,

    /// <summary>A quoted string.</summary>
    Text,

    /// <summary>A label where it is defined, or a symbol where it is given a value.</summary>
    Label,

    /// <summary>A name that is used: a label, a constant, a CSR.</summary>
    Symbol,

    Comment,

    Punctuation,

    /// <summary>Text the lexer could make nothing of.</summary>
    Wrong,
}

/// <summary>A stretch of one line that is drawn one way.</summary>
/// <param name="Start">Its offset from the start of the source.</param>
/// <param name="Flagged">Something the assembler said is about this text.</param>
public readonly record struct SourceRun(int Start, int Length, Ink Ink, bool Flagged);

/// <summary>One line of the source, ready to draw.</summary>
/// <param name="Number">From one.</param>
/// <param name="Start">The offset of its first character from the start of the source.</param>
/// <param name="Length">Its length without the line break.</param>
/// <param name="Runs">Its text in pieces, in order, covering all of it.</param>
/// <param name="Address">Where its first instruction was assembled, if it became any.</param>
/// <param name="HasProblem">The assembler has something to say about this line.</param>
public sealed record SourceLine(
    int Number, int Start, int Length, IReadOnlyList<SourceRun> Runs, uint? Address, bool HasProblem);

/// <summary>An instruction of a source line in a stage: what the gutter shows beside the line.</summary>
public readonly record struct StageMark(Stage Stage, Occupancy State);

/// <summary>
/// The source as the editor draws it: every line cut into pieces by the assembler's own lexer,
/// with what the assembler had to say marked on the text it was said about, and beside each line
/// the address it was assembled at. Nothing here knows how a piece will look, and the text is
/// never changed: a half-typed program is cut up as readily as a finished one.
/// </summary>
public sealed class SourceView
{
    private static readonly HashSet<string> Mnemonics = new(Assembler.Mnemonics, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<StageMark>> NoStages =
        new Dictionary<int, IReadOnlyList<StageMark>>();

    private readonly Program? _program;

    private SourceView(string source, Program? program, List<SourceLine> lines, IReadOnlyList<Diagnostic> diagnostics)
    {
        Source = source;
        _program = program;
        Lines = lines;
        Diagnostics = diagnostics;

        var widest = lines.Max(line => line.Address ?? 0);
        AddressDigits = Math.Max(4, widest.ToString("x", System.Globalization.CultureInfo.InvariantCulture).Length);
    }

    public string Source { get; }

    /// <summary>Every line, the empty one after a final line break included, as an editor shows it.</summary>
    public IReadOnlyList<SourceLine> Lines { get; }

    /// <summary>What the assembler said, in the order of the source.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>How many hex digits the widest address in the gutter takes; never fewer than four.</summary>
    public int AddressDigits { get; }

    public static SourceView Of(AssemblyResult assembly)
    {
        var source = assembly.Source;
        var ink = new Ink[source.Length];
        Classify(source, ink);

        var flagged = new bool[source.Length];
        var problems = new HashSet<int>();
        foreach (var diagnostic in assembly.Diagnostics)
        {
            problems.Add(diagnostic.Span.Line);
            Flag(source, flagged, diagnostic.Span);
        }

        var program = assembly.Program;
        var lines = new List<SourceLine>();
        var start = 0;
        for (var number = 1; ; number++)
        {
            var end = source.IndexOf('\n', start);
            var stop = end < 0 ? source.Length : end;

            // A carriage return before the line break belongs to the break, not to the line.
            var length = stop - start - (stop > start && source[stop - 1] == '\r' ? 1 : 0);
            uint? address = program?.SourceMap.ByLine(number).Select(entry => (uint?)entry.Address).FirstOrDefault();
            lines.Add(new SourceLine(number, start, length, Runs(ink, flagged, start, length), address, problems.Contains(number)));

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return new SourceView(source, program, lines, assembly.Diagnostics);
    }

    /// <summary>The text of a run or of a whole line.</summary>
    public string Text(int start, int length) => Source.Substring(start, length);

    /// <summary>
    /// The stages that the instructions of each line are in during a cycle, by line number, in
    /// the order of the pipeline. A line that became two instructions can be in two stages, and
    /// so can a line of a short loop.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<StageMark>> Stages(CycleRecord? record)
    {
        if (record is null || _program is null)
        {
            return NoStages;
        }

        var marks = new Dictionary<int, IReadOnlyList<StageMark>>();
        foreach (var stage in Enum.GetValues<Stage>())
        {
            var view = record[stage];
            if (view.HasInstruction && _program.SourceMap.TryGetByAddress(view.Pc, out var entry))
            {
                if (!marks.TryGetValue(entry.Line, out var list))
                {
                    marks[entry.Line] = list = new List<StageMark>();
                }

                ((List<StageMark>)list).Add(new StageMark(stage, view.State));
            }
        }

        return marks;
    }

    /// <summary>
    /// Says what each character is. A statement's first name is its mnemonic or directive unless
    /// a colon or an equals sign follows, which makes it a label; any later name is a register
    /// if it is one, and otherwise a symbol.
    /// </summary>
    private static void Classify(string source, Ink[] ink)
    {
        var tokens = Lexer.Tokenize(source, includeComments: true);
        var atStart = true;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            Ink kind;
            switch (token.Kind)
            {
                case TokenKind.EndOfFile:
                    continue;

                case TokenKind.NewLine:
                    atStart = true;
                    if (source[token.Span.Start] != ';')
                    {
                        continue;
                    }

                    kind = Ink.Punctuation;
                    break;

                case TokenKind.Comment:
                    kind = Ink.Comment;
                    break;

                case TokenKind.Identifier when Follows(tokens, i) == TokenKind.Colon:
                    kind = Ink.Label;
                    break;

                case TokenKind.Identifier when atStart && Follows(tokens, i) == TokenKind.Equals:
                    kind = Ink.Label;
                    atStart = false;
                    break;

                case TokenKind.Identifier when atStart:
                    kind = token.Text.StartsWith('.') ? Ink.Directive
                        : Mnemonics.Contains(token.Text) ? Ink.Mnemonic
                        : Ink.Plain;
                    atStart = false;
                    break;

                case TokenKind.Identifier:
                    kind = Registers.TryParse(token.Text, out _) ? Ink.Register : Ink.Symbol;
                    break;

                // A numeric local label, "1:", is a number until the colon after it says otherwise.
                case TokenKind.Number when atStart && Follows(tokens, i) == TokenKind.Colon:
                    kind = Ink.Label;
                    break;

                case TokenKind.Number:
                    kind = Ink.Number;
                    atStart = false;
                    break;

                case TokenKind.String:
                    kind = Ink.Text;
                    atStart = false;
                    break;

                case TokenKind.LocalRef:
                    kind = Ink.Symbol;
                    atStart = false;
                    break;

                case TokenKind.RelocOperator:
                    kind = Ink.Directive;
                    atStart = false;
                    break;

                case TokenKind.Error:
                    kind = Ink.Wrong;
                    atStart = false;
                    break;

                // The colon after a label leaves the statement still to begin.
                case TokenKind.Colon:
                    kind = Ink.Punctuation;
                    break;

                default:
                    kind = Ink.Punctuation;
                    atStart = false;
                    break;
            }

            var end = Math.Min(source.Length, token.Span.Start + token.Span.Length);
            for (var at = Math.Max(0, token.Span.Start); at < end; at++)
            {
                // A comment or a string that runs over several lines is that on each of them;
                // the breaks themselves are never drawn.
                ink[at] = kind;
            }
        }
    }

    /// <summary>The kind of the next token that is not a comment.</summary>
    private static TokenKind Follows(List<Token> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Comment)
            {
                return tokens[i].Kind;
            }
        }

        return TokenKind.EndOfFile;
    }

    /// <summary>
    /// Marks the text a diagnostic is about. One that points between characters, as "something
    /// is missing here" does, marks the character it points at, or the one before it at the end
    /// of a line, so that there is always something to see.
    /// </summary>
    private static void Flag(string source, bool[] flagged, SourceSpan span)
    {
        var start = Math.Clamp(span.Start, 0, source.Length);
        var end = Math.Clamp(span.Start + span.Length, start, source.Length);

        // A line break is not drawn, so one at the end of what is pointed at marks nothing.
        while (end > start && source[end - 1] is '\n' or '\r')
        {
            end--;
        }

        if (end == start)
        {
            if (start < source.Length && source[start] is not ('\n' or '\r'))
            {
                end = start + 1;
            }
            else if (start > 0 && source[start - 1] is not ('\n' or '\r'))
            {
                start--;
                end = start + 1;
            }
        }

        for (var at = start; at < end; at++)
        {
            flagged[at] = true;
        }
    }

    private static List<SourceRun> Runs(Ink[] ink, bool[] flagged, int start, int length)
    {
        var runs = new List<SourceRun>();
        var end = start + length;
        for (var at = start; at < end;)
        {
            var next = at + 1;
            while (next < end && ink[next] == ink[at] && flagged[next] == flagged[at])
            {
                next++;
            }

            runs.Add(new SourceRun(at, next - at, ink[at], flagged[at]));
            at = next;
        }

        return runs;
    }
}
