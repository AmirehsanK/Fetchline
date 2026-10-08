using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

/// <summary>A position in the token list that the statement and expression parsers share.</summary>
internal sealed class TokenCursor(List<Token> tokens)
{
    private readonly List<Token> _tokens = tokens;

    public int Position { get; set; }

    public Token Current => _tokens[Position];

    public bool AtEndOfStatement => Current.Kind is TokenKind.NewLine or TokenKind.EndOfFile;

    public Token Peek(int ahead = 1) => _tokens[Math.Min(Position + ahead, _tokens.Count - 1)];

    public Token Take()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
        {
            Position++;
        }

        return token;
    }

    /// <summary>The end of the previous token: where something that is missing should have been.</summary>
    public SourceSpan EndOfPrevious()
    {
        if (Position == 0)
        {
            return Current.Span with { Length = 0 };
        }

        var previous = _tokens[Position - 1].Span;
        return new SourceSpan(previous.Start + previous.Length, 0, previous.Line, previous.Column + previous.Length);
    }
}

/// <summary>
/// Parses expressions with C's precedence: <c>|</c>, <c>^</c>, <c>&amp;</c>, shifts, additive,
/// multiplicative, then unary. On a mistake it reports one diagnostic and returns null; the
/// caller skips to the end of the statement.
/// </summary>
internal static class ExpressionParser
{
    // One row per precedence level, loosest first.
    private static readonly (TokenKind Token, BinaryOp Op)[][] Levels =
    [
        [(TokenKind.Pipe, BinaryOp.Or)],
        [(TokenKind.Caret, BinaryOp.Xor)],
        [(TokenKind.Ampersand, BinaryOp.And)],
        [(TokenKind.ShiftLeft, BinaryOp.ShiftLeft), (TokenKind.ShiftRight, BinaryOp.ShiftRight)],
        [(TokenKind.Plus, BinaryOp.Add), (TokenKind.Minus, BinaryOp.Subtract)],
        [
            (TokenKind.Star, BinaryOp.Multiply), (TokenKind.Slash, BinaryOp.Divide),
            (TokenKind.Percent, BinaryOp.Remainder),
        ],
    ];

    public static Expr? Parse(TokenCursor cursor, DiagnosticBag diagnostics) => Binary(cursor, diagnostics, 0);

    private static Expr? Binary(TokenCursor cursor, DiagnosticBag diagnostics, int level)
    {
        if (level == Levels.Length)
        {
            return Unary(cursor, diagnostics);
        }

        var left = Binary(cursor, diagnostics, level + 1);
        while (left is not null)
        {
            BinaryOp? op = null;
            foreach (var (token, candidate) in Levels[level])
            {
                if (cursor.Current.Kind == token)
                {
                    op = candidate;
                }
            }

            if (op is null)
            {
                break;
            }

            cursor.Take();
            var right = Binary(cursor, diagnostics, level + 1);
            if (right is null)
            {
                return null;
            }

            left = new BinaryExpr(op.Value, left, right, left.Span.Through(right.Span));
        }

        return left;
    }

    private static Expr? Unary(TokenCursor cursor, DiagnosticBag diagnostics)
    {
        UnaryOp? op = cursor.Current.Kind switch
        {
            TokenKind.Minus => UnaryOp.Negate,
            TokenKind.Plus => UnaryOp.Plus,
            TokenKind.Tilde => UnaryOp.Complement,
            _ => null,
        };
        if (op is null)
        {
            return Primary(cursor, diagnostics);
        }

        var sign = cursor.Take();
        var operand = Unary(cursor, diagnostics);
        if (operand is null)
        {
            return null;
        }

        // A negative literal is one number, so that -2048 is in range where 2048 alone is not.
        if (op == UnaryOp.Negate && operand is NumberExpr number)
        {
            return new NumberExpr(unchecked(-number.Value), sign.Span.Through(operand.Span));
        }

        return new UnaryExpr(op.Value, operand, sign.Span.Through(operand.Span));
    }

    private static Expr? Primary(TokenCursor cursor, DiagnosticBag diagnostics)
    {
        var token = cursor.Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                cursor.Take();
                return new NumberExpr(token.Value, token.Span);

            case TokenKind.LocalRef:
                cursor.Take();
                return new LocalRefExpr((int)Math.Min(token.Value, int.MaxValue), token.Text == "f", token.Span);

            case TokenKind.Identifier:
                cursor.Take();
                if (token.Text == ".")
                {
                    return new HereExpr(token.Span);
                }

                if (Registers.TryParse(token.Text, out _))
                {
                    diagnostics.Error(token.Span, $"register '{token.Text}' cannot be part of an expression");
                    return null;
                }

                return new SymbolExpr(token.Text, token.Span);

            case TokenKind.LeftParen:
            {
                cursor.Take();
                var inner = Parse(cursor, diagnostics);
                if (inner is null)
                {
                    return null;
                }

                if (!Closing(cursor, diagnostics, token, out var close))
                {
                    return null;
                }

                return inner with { Span = token.Span.Through(close) };
            }

            case TokenKind.RelocOperator:
            {
                cursor.Take();
                var open = cursor.Take();   // The lexer only makes this token when a '(' follows.
                var inner = Parse(cursor, diagnostics);
                if (inner is null || !Closing(cursor, diagnostics, open, out var close))
                {
                    return null;
                }

                var kind = token.Text switch
                {
                    "hi" => RelocKind.Hi,
                    "lo" => RelocKind.Lo,
                    "pcrel_hi" => RelocKind.PcrelHi,
                    _ => RelocKind.PcrelLo,
                };
                return new RelocExpr(kind, inner, token.Span.Through(close));
            }

            case TokenKind.Error:
                cursor.Take();
                diagnostics.Error(token.Span, token.Text);
                return null;

            case TokenKind.String:
                diagnostics.Error(token.Span, "a string cannot be used as a number");
                return null;

            default:
                diagnostics.Error(
                    cursor.AtEndOfStatement ? cursor.EndOfPrevious() : token.Span,
                    "expected a number, a symbol or '('");
                return null;
        }
    }

    private static bool Closing(TokenCursor cursor, DiagnosticBag diagnostics, Token open, out SourceSpan close)
    {
        if (cursor.Current.Kind == TokenKind.RightParen)
        {
            close = cursor.Take().Span;
            return true;
        }

        close = default;
        diagnostics.Error(
            cursor.AtEndOfStatement ? cursor.EndOfPrevious() : cursor.Current.Span,
            "expected ')'",
            $"to close the '(' at column {open.Span.Column}");
        return false;
    }
}
