using System.Collections.Frozen;
using System.Globalization;
using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

/// <summary>
/// Turns source into statements. It knows the shape of a line (labels, a name, operands separated
/// by commas) but not what any instruction or directive means; that is the assembler's business.
/// A statement with a mistake in it is reported once, marked broken, and skipped to its end, so
/// one bad line costs one diagnostic and the lines after it are still checked.
/// </summary>
public static class Parser
{
    // Directives whose operands are not expressions (flags, @types, quoted file names). They are
    // accepted so that compiler output assembles, and everything after the name is skipped.
    private static readonly FrozenSet<string> Unparsed =
        new[] { ".option", ".type", ".size", ".file", ".attribute", ".ident", ".local", ".weak" }
            .ToFrozenSet(StringComparer.Ordinal);

    public static List<Statement> Parse(string source, DiagnosticBag diagnostics)
    {
        var cursor = new TokenCursor(Lexer.Tokenize(source));
        var statements = new List<Statement>();

        while (cursor.Current.Kind != TokenKind.EndOfFile)
        {
            if (cursor.Current.Kind == TokenKind.NewLine)
            {
                cursor.Take();
                continue;
            }

            statements.Add(ParseStatement(cursor, diagnostics));
            SkipToEndOfStatement(cursor);
        }

        return statements;
    }

    private static Statement ParseStatement(TokenCursor cursor, DiagnosticBag diagnostics)
    {
        var labels = new List<LabelDefinition>();
        var broken = false;

        while (cursor.Peek().Kind == TokenKind.Colon
            && cursor.Current.Kind is TokenKind.Identifier or TokenKind.Number)
        {
            var token = cursor.Take();
            cursor.Take();
            if (token.Kind == TokenKind.Number)
            {
                labels.Add(new LabelDefinition(
                    token.Value.ToString(CultureInfo.InvariantCulture), (int)Math.Min(token.Value, int.MaxValue), token.Span));
            }
            else if (Registers.TryParse(token.Text, out _))
            {
                diagnostics.Error(token.Span, $"'{token.Text}' is a register and cannot be a label");
                broken = true;
            }
            else
            {
                labels.Add(new LabelDefinition(token.Text, -1, token.Span));
            }
        }

        var first = cursor.Current;
        if (cursor.AtEndOfStatement)
        {
            return new Statement(labels, StatementKind.Empty, string.Empty, first.Span, [], first.Span, broken);
        }

        if (first.Kind != TokenKind.Identifier)
        {
            diagnostics.Error(
                first.Span,
                first.Kind == TokenKind.Error ? first.Text : "expected a label, an instruction or a directive");
            return new Statement(labels, StatementKind.Empty, string.Empty, first.Span, [], first.Span, true);
        }

        cursor.Take();
        if (cursor.Current.Kind == TokenKind.Equals)
        {
            cursor.Take();
            var value = ExpressionParser.Parse(cursor, diagnostics);
            broken |= value is null || !ExpectEnd(cursor, diagnostics);
            Operand[] operands = value is null ? [] : [new ExprOperand(value, value.Span)];
            var span = value is null ? first.Span : first.Span.Through(value.Span);
            return new Statement(labels, StatementKind.Assignment, first.Text, first.Span, operands, span, broken);
        }

        var name = first.Text.ToLowerInvariant();
        var kind = name.StartsWith('.') ? StatementKind.Directive : StatementKind.Instruction;

        if (kind == StatementKind.Directive && Unparsed.Contains(name))
        {
            return new Statement(labels, kind, name, first.Span, [], first.Span, broken);
        }

        if (name == ".section")
        {
            // Only the section's name matters here; its flags and type are for a linker.
            var section = cursor.Current;
            if (section.Kind != TokenKind.Identifier)
            {
                diagnostics.Error(cursor.AtEndOfStatement ? cursor.EndOfPrevious() : section.Span, "expected a section name");
                return new Statement(labels, kind, name, first.Span, [], first.Span, true);
            }

            cursor.Take();
            Operand operand = new ExprOperand(new SymbolExpr(section.Text, section.Span), section.Span);
            return new Statement(labels, kind, name, first.Span, [operand], first.Span.Through(section.Span), broken);
        }

        var parsed = new List<Operand>();
        if (!cursor.AtEndOfStatement)
        {
            while (true)
            {
                var operand = ParseOperand(cursor, diagnostics);
                if (operand is null)
                {
                    broken = true;
                    break;
                }

                parsed.Add(operand);
                if (cursor.Current.Kind != TokenKind.Comma)
                {
                    broken |= !ExpectEnd(cursor, diagnostics);
                    break;
                }

                cursor.Take();
                if (cursor.AtEndOfStatement)
                {
                    diagnostics.Error(cursor.EndOfPrevious(), "expected an operand after ','");
                    broken = true;
                    break;
                }
            }
        }

        var whole = parsed.Count == 0 ? first.Span : first.Span.Through(parsed[^1].Span);
        return new Statement(labels, kind, name, first.Span, parsed, whole, broken);
    }

    private static Operand? ParseOperand(TokenCursor cursor, DiagnosticBag diagnostics)
    {
        var token = cursor.Current;
        if (token.Kind == TokenKind.String)
        {
            cursor.Take();
            return new StringOperand(token.Bytes!, token.Span);
        }

        if (token.Kind == TokenKind.Identifier && Registers.TryParse(token.Text, out var register))
        {
            cursor.Take();
            return new RegisterOperand(register, token.Span);
        }

        // "(a0)" is a base register with no offset. Any other "(" starts an expression.
        Expr? offset = null;
        if (!(token.Kind == TokenKind.LeftParen && IsRegister(cursor.Peek())))
        {
            offset = ExpressionParser.Parse(cursor, diagnostics);
            if (offset is null)
            {
                return null;
            }

            if (cursor.Current.Kind != TokenKind.LeftParen)
            {
                return new ExprOperand(offset, offset.Span);
            }
        }

        cursor.Take();
        var baseToken = cursor.Current;
        if (!IsRegister(baseToken))
        {
            diagnostics.Error(
                cursor.AtEndOfStatement ? cursor.EndOfPrevious() : baseToken.Span,
                "expected a base register",
                baseToken.Kind == TokenKind.Identifier ? Suggest.Hint(baseToken.Text, Registers.AllNames) : null);
            return null;
        }

        cursor.Take();
        Registers.TryParse(baseToken.Text, out var baseRegister);
        if (cursor.Current.Kind != TokenKind.RightParen)
        {
            diagnostics.Error(cursor.AtEndOfStatement ? cursor.EndOfPrevious() : cursor.Current.Span, "expected ')'");
            return null;
        }

        var close = cursor.Take();
        return new MemoryOperand(offset, baseRegister, (offset?.Span ?? token.Span).Through(close.Span));
    }

    private static bool IsRegister(Token token) =>
        token.Kind == TokenKind.Identifier && Registers.TryParse(token.Text, out _);

    private static bool ExpectEnd(TokenCursor cursor, DiagnosticBag diagnostics)
    {
        if (cursor.AtEndOfStatement)
        {
            return true;
        }

        var token = cursor.Current;
        diagnostics.Error(token.Span, token.Kind == TokenKind.Error ? token.Text : "expected ',' or the end of the line");
        return false;
    }

    private static void SkipToEndOfStatement(TokenCursor cursor)
    {
        while (!cursor.AtEndOfStatement)
        {
            cursor.Take();
        }
    }
}
