using Fetchline.Core.Asm;

namespace Fetchline.Tests.Asm;

public class ExpressionTests
{
    /// <summary>A scope with a few symbols: two labels in section 0, one in section 1, one constant.</summary>
    private sealed class Scope(bool final) : IExprScope
    {
        public bool Final { get; } = final;

        public DiagnosticBag Diagnostics { get; } = new();

        public Value Here => Value.Relative(0, 0x40);

        public Value Lookup(SymbolExpr symbol) => symbol.Name switch
        {
            "start" => Value.Relative(0, 0x10),
            "end" => Value.Relative(0, 0x30),
            "data" => Value.Relative(1, 0x8),
            "SIZE" => Value.Absolute(64),
            _ => Value.Unknown,
        };

        public Value LookupLocal(LocalRefExpr reference) =>
            Value.Relative(0, reference.Forward ? 0x100 + reference.Number : 0x200 + reference.Number);

        public Value Reloc(RelocExpr expression, Value operand) =>
            Final && operand.IsAbsolute ? Value.Absolute(operand.Number & 0xFFF) : Value.Unknown;
    }

    private static (Expr? Expr, DiagnosticBag Diagnostics, TokenCursor Cursor) Parse(string source)
    {
        var diagnostics = new DiagnosticBag();
        var cursor = new TokenCursor(Lexer.Tokenize(source));
        return (ExpressionParser.Parse(cursor, diagnostics), diagnostics, cursor);
    }

    private static Value Evaluate(string source, bool final = false)
    {
        var (expr, diagnostics, cursor) = Parse(source);
        Assert.Empty(diagnostics.Items);
        Assert.True(cursor.AtEndOfStatement, $"'{source}' was not consumed to its end");
        return Evaluator.Evaluate(expr!, new Scope(final));
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("-42", -42L)]
    [InlineData("+7", 7L)]
    [InlineData("~0", -1L)]
    [InlineData("- - 5", 5L)]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("10 - 4 - 3", 3L)]
    [InlineData("100 / 7", 14L)]
    [InlineData("-100 / 7", -14L)]
    [InlineData("100 % 7", 2L)]
    [InlineData("-100 % 7", -2L)]
    [InlineData("1 << 12", 4096L)]
    [InlineData("-16 >> 2", -4L)]
    [InlineData("1 << 4 + 1", 32L)]
    [InlineData("0xF0 | 0x0F", 255L)]
    [InlineData("0xFF & 0x0F", 15L)]
    [InlineData("0xFF ^ 0x0F", 240L)]
    [InlineData("1 | 2 ^ 3 & 4", 3L)]
    [InlineData("2 + 3 & 4", 4L)]
    [InlineData("'a' + 1", 98L)]
    [InlineData("-2 * -3", 6L)]
    [InlineData("-(2 + 3)", -5L)]
    [InlineData("SIZE / 4 - 1", 15L)]
    [InlineData("0x7FFFFFFFFFFFFFFF + 1", long.MinValue)]
    [InlineData("-0x8000000000000000 / -1", long.MinValue)]
    public void ConstantsEvaluateWithTheUsualPrecedence(string source, long expected)
    {
        Assert.Equal(Value.Absolute(expected), Evaluate(source));
    }

    [Fact]
    public void ALabelIsAnOffsetIntoItsSectionUntilTheLayoutIsKnown()
    {
        Assert.Equal(Value.Relative(0, 0x10), Evaluate("start"));
        Assert.Equal(Value.Relative(0, 0x14), Evaluate("start + 4"));
        Assert.Equal(Value.Relative(0, 0x14), Evaluate("4 + start"));
        Assert.Equal(Value.Relative(0, 0x0C), Evaluate("start - 4"));
        Assert.Equal(Value.Relative(0, 0x40), Evaluate("."));
        Assert.Equal(Value.Relative(0, 0x101), Evaluate("1f"));
        Assert.Equal(Value.Relative(0, 0x202), Evaluate("2b"));
    }

    [Fact]
    public void TheDistanceBetweenTwoLabelsOfOneSectionIsAPlainNumber()
    {
        Assert.Equal(Value.Absolute(0x20), Evaluate("end - start"));
        Assert.Equal(Value.Absolute(0x30), Evaluate(". - start"));
        Assert.Equal(Value.Absolute(8), Evaluate("(end - start) / 4"));
    }

    [Theory]
    [InlineData("start + end")]       // the sum of two addresses means nothing
    [InlineData("data - start")]      // labels in different sections: the distance is not known yet
    [InlineData("4 - start")]
    [InlineData("start * 2")]
    [InlineData("-start")]
    [InlineData("start & 0xFFF")]
    [InlineData("undefined + 1")]
    [InlineData("%lo(start)")]
    public void WhatCannotBeKnownInTheFirstPassIsUnknownAndNotAnError(string source)
    {
        var (expr, diagnostics, _) = Parse(source);
        var scope = new Scope(final: false);

        Assert.Equal(Value.Unknown, Evaluator.Evaluate(expr!, scope));
        Assert.Empty(diagnostics.Items);
        Assert.Empty(scope.Diagnostics.Items);
    }

    [Theory]
    [InlineData("1 / 0", 5, "division by zero")]
    [InlineData("1 % (2 - 2)", 5, "division by zero")]
    [InlineData("1 << 64", 6, "a shift by 64 is outside 0 to 63")]
    [InlineData("1 >> -1", 6, "a shift by -1 is outside 0 to 63")]
    public void ArithmeticThatHasNoAnswerIsReportedInTheFinalPassOnly(string source, int column, string message)
    {
        var (expr, _, _) = Parse(source);

        var first = new Scope(final: false);
        Assert.Equal(Value.Unknown, Evaluator.Evaluate(expr!, first));
        Assert.Empty(first.Diagnostics.Items);

        var second = new Scope(final: true);
        Assert.Equal(Value.Unknown, Evaluator.Evaluate(expr!, second));
        var diagnostic = Assert.Single(second.Diagnostics.Items);
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(column, diagnostic.Span.Column);
    }

    [Fact]
    public void ARelocationOperatorIsAppliedByTheScope()
    {
        Assert.Equal(Value.Absolute(0x345), Evaluate("%lo(0x12345)", final: true));
        Assert.Equal(Value.Absolute(0x346), Evaluate("%pcrel_lo(0x12345) + 1", final: true));
    }

    [Theory]
    [InlineData("1 +", 4, "expected a number, a symbol or '('")]
    [InlineData("", 1, "expected a number, a symbol or '('")]
    [InlineData("(1 + 2", 7, "expected ')'")]
    [InlineData("(1 + 2 3", 8, "expected ')'")]
    [InlineData("%hi(x", 6, "expected ')'")]
    [InlineData("1 + a0", 5, "register 'a0' cannot be part of an expression")]
    [InlineData("4 * \"four\"", 5, "a string cannot be used as a number")]
    [InlineData("1 + 08", 5, "'08' is not a number")]
    [InlineData("3 * , 4", 5, "expected a number, a symbol or '('")]
    public void AMistakeIsReportedOnceAtItsColumn(string source, int column, string message)
    {
        var (expr, diagnostics, _) = Parse(source);

        Assert.Null(expr);
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(column, diagnostic.Span.Column);
        Assert.Equal(Severity.Error, diagnostic.Severity);
    }

    [Fact]
    public void AnExpressionStopsWhereAnOperandContinues()
    {
        // "8(sp)" is an offset and a base register: the expression is the 8.
        var (expr, diagnostics, cursor) = Parse("8(sp)");

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new NumberExpr(8, expr!.Span), expr);
        Assert.Equal(TokenKind.LeftParen, cursor.Current.Kind);

        (expr, diagnostics, cursor) = Parse("%lo(msg)(a0), 5");
        Assert.Empty(diagnostics.Items);
        Assert.IsType<RelocExpr>(expr);
        Assert.Equal(TokenKind.LeftParen, cursor.Current.Kind);
    }

    [Fact]
    public void AnExpressionKnowsTheTextItCameFrom()
    {
        const string source = "  (start + 4) * 2";
        var (expr, _, _) = Parse(source);

        Assert.Equal("(start + 4) * 2", source.Substring(expr!.Span.Start, expr.Span.Length));
        var product = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal("(start + 4)", source.Substring(product.Left.Span.Start, product.Left.Span.Length));
    }
}
