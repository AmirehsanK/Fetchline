using System.Text;

namespace Fetchline.Core.Asm;

public enum TokenKind : byte
{
    EndOfFile,

    /// <summary>The end of a statement: a line break, or a <c>;</c>.</summary>
    NewLine,

    /// <summary>A mnemonic, register, symbol or directive. Dots, <c>_</c> and <c>$</c> are letters.</summary>
    Identifier,

    /// <summary>A number or a character constant.</summary>
    Number,

    /// <summary>A quoted string, already unescaped into bytes.</summary>
    String,

    /// <summary>A reference to a numeric local label: <c>1b</c> or <c>1f</c>.</summary>
    LocalRef,

    /// <summary><c>%hi</c>, <c>%lo</c>, <c>%pcrel_hi</c> or <c>%pcrel_lo</c>, directly before a <c>(</c>.</summary>
    RelocOperator,

    Comma, LeftParen, RightParen, Colon, Equals,
    Plus, Minus, Star, Slash, Percent, ShiftLeft, ShiftRight, Ampersand, Pipe, Caret, Tilde,

    /// <summary>A comment. Only produced when asked for; the assembler never sees one.</summary>
    Comment,

    /// <summary>Text the assembler has no use for. <see cref="Token.Text"/> says what is wrong.</summary>
    Error,
}

/// <summary>One token and where it is.</summary>
public readonly record struct Token(TokenKind Kind, SourceSpan Span)
{
    /// <summary>
    /// An identifier's text; a relocation operator's name without the <c>%</c>; <c>b</c> or
    /// <c>f</c> for a local reference; the message of an error token.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>A number's value, or the number of a local label.</summary>
    public long Value { get; init; }

    /// <summary>The bytes of a string, with escapes resolved and other characters as UTF-8.</summary>
    public byte[]? Bytes { get; init; }
}

/// <summary>
/// Splits assembly source into tokens. It never fails: text it cannot use becomes an
/// <see cref="TokenKind.Error"/> token, so the same lexer can colour a half-typed program in the
/// editor and feed the assembler.
/// </summary>
public static class Lexer
{
    private static readonly string[] RelocOperators = ["pcrel_hi", "pcrel_lo", "hi", "lo"];

    public static List<Token> Tokenize(string source, bool includeComments = false)
    {
        var tokens = new List<Token>();
        var scanner = new Scanner(source, tokens, includeComments);
        scanner.Run();
        return tokens;
    }

    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c is '_' or '.' or '$';

    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$';

    private ref struct Scanner(string source, List<Token> tokens, bool includeComments)
    {
        private readonly string _source = source;
        private readonly List<Token> _tokens = tokens;
        private readonly bool _includeComments = includeComments;
        private int _position;
        private int _line = 1;
        private int _lineStart;

        public void Run()
        {
            while (_position < _source.Length)
            {
                var c = _source[_position];
                switch (c)
                {
                    case ' ' or '\t' or '\r' or '\f' or '\v':
                        _position++;
                        break;
                    case '\n':
                        Add(TokenKind.NewLine, _position, 1);
                        _position++;
                        _line++;
                        _lineStart = _position;
                        break;
                    case ';':
                        Add(TokenKind.NewLine, _position++, 1);
                        break;
                    case '#':
                        LineComment();
                        break;
                    case '/' when Peek(1) == '/':
                        LineComment();
                        break;
                    case '/' when Peek(1) == '*':
                        BlockComment();
                        break;
                    case '"':
                        String();
                        break;
                    case '\'':
                        Character();
                        break;
                    case '%':
                        PercentOrRelocation();
                        break;
                    case '<' when Peek(1) == '<':
                        Add(TokenKind.ShiftLeft, _position, 2);
                        _position += 2;
                        break;
                    case '>' when Peek(1) == '>':
                        Add(TokenKind.ShiftRight, _position, 2);
                        _position += 2;
                        break;
                    default:
                        if (IsIdentifierStart(c))
                        {
                            Identifier();
                        }
                        else if (char.IsAsciiDigit(c))
                        {
                            Number();
                        }
                        else if (Punctuation(c) is { } kind)
                        {
                            Add(kind, _position++, 1);
                        }
                        else
                        {
                            // A surrogate pair is one character to the reader, so it is one token.
                            var length = char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
                            Error(_position, length, $"unexpected character '{_source.AsSpan(_position, length)}'");
                            _position += length;
                        }

                        break;
                }
            }

            Add(TokenKind.EndOfFile, _position, 0);
        }

        private static TokenKind? Punctuation(char c) => c switch
        {
            ',' => TokenKind.Comma,
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            ':' => TokenKind.Colon,
            '=' => TokenKind.Equals,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '&' => TokenKind.Ampersand,
            '|' => TokenKind.Pipe,
            '^' => TokenKind.Caret,
            '~' => TokenKind.Tilde,
            _ => null,
        };

        private readonly char Peek(int ahead) =>
            _position + ahead < _source.Length ? _source[_position + ahead] : '\0';

        private readonly SourceSpan Span(int start, int length) => new(start, length, _line, start - _lineStart + 1);

        private readonly void Add(TokenKind kind, int start, int length) => _tokens.Add(new Token(kind, Span(start, length)));

        private readonly void Error(int start, int length, string message) =>
            _tokens.Add(new Token(TokenKind.Error, Span(start, length)) { Text = message });

        private void LineComment()
        {
            var start = _position;
            while (_position < _source.Length && _source[_position] is not ('\n' or '\r'))
            {
                _position++;
            }

            if (_includeComments)
            {
                Add(TokenKind.Comment, start, _position - start);
            }
        }

        // A block comment may cover several lines. It is reported one line at a time, because a
        // span never crosses a line break.
        private void BlockComment()
        {
            var start = _position;
            if (_source.IndexOf("*/", _position + 2, StringComparison.Ordinal) < 0)
            {
                // The error sits on the opening mark, and tokens stay in source order, so it comes
                // before the rest of the file, which is all comment.
                Error(_position, 2, "this comment is never closed");
                start += 2;
            }

            _position += 2;
            while (true)
            {
                if (_position >= _source.Length)
                {
                    CommentPiece(start);
                    return;
                }

                if (_source[_position] == '*' && Peek(1) == '/')
                {
                    _position += 2;
                    CommentPiece(start);
                    return;
                }

                if (_source[_position] == '\n')
                {
                    CommentPiece(start);
                    _position++;
                    _line++;
                    _lineStart = _position;
                    start = _position;
                }
                else
                {
                    _position++;
                }
            }
        }

        private readonly void CommentPiece(int start)
        {
            var length = _position - start;
            if (_includeComments && length > 0)
            {
                Add(TokenKind.Comment, start, length);
            }
        }

        private void Identifier()
        {
            var start = _position;
            while (_position < _source.Length && IsIdentifierPart(_source[_position]))
            {
                _position++;
            }

            _tokens.Add(new Token(TokenKind.Identifier, Span(start, _position - start))
            {
                Text = _source[start.._position],
            });
        }

        private void Number()
        {
            var start = _position;
            var radix = 10;
            if (_source[_position] == '0')
            {
                var next = char.ToLowerInvariant(Peek(1));
                if (next == 'x')
                {
                    radix = 16;
                    _position += 2;
                }
                else if (next == 'b' && Peek(2) is '0' or '1')
                {
                    radix = 2;
                    _position += 2;
                }
                else if (char.IsAsciiDigit(Peek(1)))
                {
                    // As in GNU as and C, a leading zero means octal.
                    radix = 8;
                    _position++;
                }
            }

            var digitsStart = _position;
            ulong value = 0;
            var overflow = false;
            var badDigit = false;
            while (_position < _source.Length && char.IsAsciiHexDigit(_source[_position]))
            {
                var c = _source[_position];
                var digit = char.IsAsciiDigit(c) ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;
                if (digit >= radix)
                {
                    // 'b' and 'f' after decimal digits make a local label reference, not a bad digit.
                    if (radix == 10 && char.ToLowerInvariant(c) is 'b' or 'f')
                    {
                        break;
                    }

                    badDigit = true;
                }

                var next = unchecked((value * (ulong)radix) + (ulong)digit);
                overflow |= value > (ulong.MaxValue - (ulong)digit) / (ulong)radix;
                value = next;
                _position++;
            }

            if (radix == 10 && _position < _source.Length && _source[_position] is 'b' or 'f'
                && !IsIdentifierPart(Peek(1)) && _position > digitsStart)
            {
                var direction = _source[_position].ToString();
                _position++;
                _tokens.Add(new Token(TokenKind.LocalRef, Span(start, _position - start))
                {
                    Text = direction,
                    Value = (long)value,
                });
                return;
            }

            var noDigits = _position == digitsStart;
            var trailing = false;
            while (_position < _source.Length && IsIdentifierPart(_source[_position]))
            {
                trailing = true;
                _position++;
            }

            var text = _source.AsSpan(start, _position - start);
            if (noDigits || trailing || badDigit)
            {
                Error(start, _position - start, $"'{text}' is not a number");
            }
            else if (overflow)
            {
                Error(start, _position - start, $"'{text}' does not fit in 64 bits");
            }
            else
            {
                _tokens.Add(new Token(TokenKind.Number, Span(start, _position - start)) { Value = (long)value });
            }
        }

        private void Character()
        {
            var start = _position;
            _position++;
            if (_position >= _source.Length || _source[_position] is '\n' or '\r')
            {
                Error(start, 1, "a character constant needs a character: 'a'");
                return;
            }

            long value;
            if (_source[_position] == '\\')
            {
                if (!Escape(out var escaped))
                {
                    SkipToClosingQuote();
                    Error(start, _position - start, "unknown escape in a character constant");
                    return;
                }

                value = escaped;
            }
            else if (char.IsHighSurrogate(_source[_position]) && char.IsLowSurrogate(Peek(1)))
            {
                value = char.ConvertToUtf32(_source[_position], _source[_position + 1]);
                _position += 2;
            }
            else
            {
                value = _source[_position++];
            }

            if (Peek(0) != '\'')
            {
                SkipToClosingQuote();
                Error(start, _position - start, "a character constant holds one character and ends with '");
                return;
            }

            _position++;
            _tokens.Add(new Token(TokenKind.Number, Span(start, _position - start)) { Value = value });
        }

        // After a bad character constant, its closing quote would otherwise start another one.
        private void SkipToClosingQuote()
        {
            var end = _position;
            while (end < _source.Length && _source[end] is not ('\n' or '\r' or '\''))
            {
                end++;
            }

            if (end < _source.Length && _source[end] == '\'')
            {
                _position = end + 1;
            }
        }

        private void String()
        {
            var start = _position;
            _position++;
            var bytes = new List<byte>();
            Span<byte> utf8 = stackalloc byte[4];

            while (true)
            {
                if (_position >= _source.Length || _source[_position] is '\n' or '\r')
                {
                    Error(start, _position - start, "this string is never closed");
                    return;
                }

                var c = _source[_position];
                if (c == '"')
                {
                    _position++;
                    break;
                }

                if (c == '\\')
                {
                    var escapeStart = _position;
                    if (!Escape(out var escaped))
                    {
                        Error(escapeStart, _position - escapeStart, "unknown escape in a string");
                        SkipToStringEnd();
                        return;
                    }

                    bytes.Add(escaped);
                    continue;
                }

                var length = char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
                var written = Encoding.UTF8.GetBytes(_source.AsSpan(_position, length), utf8);
                bytes.AddRange(utf8[..written]);
                _position += length;
            }

            _tokens.Add(new Token(TokenKind.String, Span(start, _position - start)) { Bytes = [.. bytes] });
        }

        private void SkipToStringEnd()
        {
            while (_position < _source.Length && _source[_position] is not ('\n' or '\r'))
            {
                if (_source[_position++] == '"')
                {
                    return;
                }
            }
        }

        /// <summary>Reads an escape that starts at the backslash; leaves the position after it.</summary>
        private bool Escape(out byte value)
        {
            value = 0;
            _position++;
            if (_position >= _source.Length)
            {
                return false;
            }

            var c = _source[_position++];
            switch (c)
            {
                case 'n': value = (byte)'\n'; return true;
                case 't': value = (byte)'\t'; return true;
                case 'r': value = (byte)'\r'; return true;
                case 'b': value = (byte)'\b'; return true;
                case 'f': value = (byte)'\f'; return true;
                case '\\' or '"' or '\'': value = (byte)c; return true;
                case 'x':
                {
                    var digits = 0;
                    var number = 0;
                    while (digits < 2 && _position < _source.Length && char.IsAsciiHexDigit(_source[_position]))
                    {
                        number = (number * 16) + Convert.ToInt32(_source[_position++].ToString(), 16);
                        digits++;
                    }

                    value = (byte)number;
                    return digits > 0;
                }

                case >= '0' and <= '7':
                {
                    var number = c - '0';
                    var digits = 1;
                    while (digits < 3 && _position < _source.Length && _source[_position] is >= '0' and <= '7')
                    {
                        number = (number * 8) + (_source[_position++] - '0');
                        digits++;
                    }

                    value = (byte)number;
                    return number <= 255;
                }

                default:
                    return false;
            }
        }

        // '%' is the remainder operator unless it begins %hi( and its relatives.
        private void PercentOrRelocation()
        {
            foreach (var name in RelocOperators)
            {
                var end = _position + 1 + name.Length;
                if (string.CompareOrdinal(_source, _position + 1, name, 0, name.Length) != 0
                    || (end < _source.Length && IsIdentifierPart(_source[end])))
                {
                    continue;
                }

                var after = end;
                while (after < _source.Length && _source[after] is ' ' or '\t')
                {
                    after++;
                }

                if (after < _source.Length && _source[after] == '(')
                {
                    _tokens.Add(new Token(TokenKind.RelocOperator, Span(_position, end - _position)) { Text = name });
                    _position = end;
                    return;
                }
            }

            Add(TokenKind.Percent, _position++, 1);
        }
    }
}
