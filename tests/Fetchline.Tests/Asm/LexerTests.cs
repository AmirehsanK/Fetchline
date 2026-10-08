using Fetchline.Core.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Asm;

public class LexerTests
{
    private static TokenKind[] Kinds(string source) => [.. Lexer.Tokenize(source).Select(t => t.Kind)];

    [Fact]
    public void AnInstructionLineBecomesItsTokens()
    {
        var tokens = Lexer.Tokenize("  addi a0, a1, -5   # comment");

        Assert.Equal(
            [
                TokenKind.Identifier, TokenKind.Identifier, TokenKind.Comma, TokenKind.Identifier,
                TokenKind.Comma, TokenKind.Minus, TokenKind.Number, TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
        Assert.Equal("addi", tokens[0].Text);
        Assert.Equal("a0", tokens[1].Text);
        Assert.Equal(5, tokens[6].Value);
    }

    [Fact]
    public void EveryTokenKnowsItsLineAndColumn()
    {
        var tokens = Lexer.Tokenize("main:\n\tlw a0, 8(sp)\r\n  ret");

        var lw = tokens.Single(t => t.Text == "lw");
        Assert.Equal((2, 2), (lw.Span.Line, lw.Span.Column));

        var sp = tokens.Single(t => t.Text == "sp");
        Assert.Equal((2, 11, 2), (sp.Span.Line, sp.Span.Column, sp.Span.Length));
        Assert.Equal("sp", "main:\n\tlw a0, 8(sp)\r\n  ret".Substring(sp.Span.Start, sp.Span.Length));

        var ret = tokens.Single(t => t.Text == "ret");
        Assert.Equal((3, 3), (ret.Span.Line, ret.Span.Column));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("0x1F", 31L)]
    [InlineData("0XfF", 255L)]
    [InlineData("0b101", 5L)]
    [InlineData("017", 15L)]
    [InlineData("0xFFFFFFFF", 4294967295L)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]
    [InlineData("1234567890123", 1234567890123L)]
    [InlineData("'a'", 97L)]
    [InlineData("'\\n'", 10L)]
    [InlineData("'\\0'", 0L)]
    [InlineData("'\\''", 39L)]
    [InlineData("'\\x41'", 65L)]
    public void NumbersInEveryNotation(string source, long expected)
    {
        var token = Lexer.Tokenize(source)[0];

        Assert.Equal(TokenKind.Number, token.Kind);
        Assert.Equal(expected, token.Value);
        Assert.Equal(source.Length, token.Span.Length);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("08")]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("12e5")]
    [InlineData("0x1FFFFFFFFFFFFFFFF")]
    [InlineData("99999999999999999999999")]
    [InlineData("'ab'")]
    [InlineData("'")]
    [InlineData("'\\q'")]
    [InlineData("@")]
    [InlineData("<")]
    [InlineData("\"never closed")]
    [InlineData("\"bad \\q escape\"")]
    [InlineData("/* never closed")]
    public void WhatIsNotATokenBecomesAnErrorTokenWithAReason(string source)
    {
        var tokens = Lexer.Tokenize(source);

        var error = Assert.Single(tokens, t => t.Kind == TokenKind.Error);
        Assert.NotEmpty(error.Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void LocalLabelReferencesAreToldApartFromBinaryNumbers()
    {
        var tokens = Lexer.Tokenize("1b 1f 12f 0b 0b1 0f");

        Assert.Equal(
            [
                TokenKind.LocalRef, TokenKind.LocalRef, TokenKind.LocalRef, TokenKind.LocalRef,
                TokenKind.Number, TokenKind.LocalRef, TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
        Assert.Equal((1L, "b"), (tokens[0].Value, tokens[0].Text));
        Assert.Equal((1L, "f"), (tokens[1].Value, tokens[1].Text));
        Assert.Equal((12L, "f"), (tokens[2].Value, tokens[2].Text));
        Assert.Equal((0L, "b"), (tokens[3].Value, tokens[3].Text));
        Assert.Equal(1L, tokens[4].Value);
    }

    [Fact]
    public void StringsAreUnescapedIntoBytes()
    {
        var plain = Lexer.Tokenize("\"Hi\\n\"")[0];
        Assert.Equal(TokenKind.String, plain.Kind);
        Assert.Equal("Hi\n"u8.ToArray(), plain.Bytes);

        var escapes = Lexer.Tokenize("\"a\\x41\\101\\t\\\"\\\\\\0\"")[0];
        Assert.Equal(new byte[] { (byte)'a', 0x41, 0x41, 9, (byte)'"', (byte)'\\', 0 }, escapes.Bytes);

        var empty = Lexer.Tokenize("\"\"")[0];
        Assert.Equal(TokenKind.String, empty.Kind);
        Assert.Empty(empty.Bytes!);
    }

    [Fact]
    public void TextOutsideAsciiInAStringIsStoredAsUtf8()
    {
        var token = Lexer.Tokenize("\"سلام é 😀\"")[0];

        Assert.Equal("سلام é 😀"u8.ToArray(), token.Bytes);
    }

    [Fact]
    public void PercentIsARelocationOperatorOnlyDirectlyBeforeAParenthesis()
    {
        Assert.Equal(
            [TokenKind.RelocOperator, TokenKind.LeftParen, TokenKind.Identifier, TokenKind.RightParen, TokenKind.EndOfFile],
            Kinds("%hi(msg)"));
        Assert.Equal("pcrel_lo", Lexer.Tokenize("%pcrel_lo (1b)")[0].Text);
        Assert.Equal("pcrel_hi", Lexer.Tokenize("%pcrel_hi(x)")[0].Text);
        Assert.Equal("lo", Lexer.Tokenize("%lo(x)")[0].Text);

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Percent, TokenKind.Identifier, TokenKind.EndOfFile],
            Kinds("a % hi"));
        Assert.Equal([TokenKind.Percent, TokenKind.Identifier, TokenKind.EndOfFile], Kinds("%hi"));
        Assert.Equal(
            [TokenKind.Percent, TokenKind.Identifier, TokenKind.LeftParen, TokenKind.Number, TokenKind.RightParen, TokenKind.EndOfFile],
            Kinds("%high(1)"));
    }

    [Fact]
    public void OperatorsAndPunctuation()
    {
        Assert.Equal(
            [
                TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
                TokenKind.ShiftLeft, TokenKind.ShiftRight, TokenKind.Ampersand, TokenKind.Pipe,
                TokenKind.Caret, TokenKind.Tilde, TokenKind.LeftParen, TokenKind.RightParen,
                TokenKind.Comma, TokenKind.Colon, TokenKind.Equals, TokenKind.EndOfFile,
            ],
            Kinds("+ - * / % << >> & | ^ ~ ( ) , : ="));
    }

    [Fact]
    public void IdentifiersMayHoldDotsUnderscoresAndDollars()
    {
        var tokens = Lexer.Tokenize(".text fence.i _start .L1 main.loop $t0 .");

        Assert.Equal(
            [".text", "fence.i", "_start", ".L1", "main.loop", "$t0", "."],
            tokens.Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text));
    }

    [Fact]
    public void ASemicolonEndsAStatementLikeALineBreak()
    {
        Assert.Equal(
            [TokenKind.Identifier, TokenKind.NewLine, TokenKind.Identifier, TokenKind.NewLine, TokenKind.EndOfFile],
            Kinds("nop; ret\n"));
    }

    [Fact]
    public void CommentsAreDroppedUnlessAskedFor()
    {
        const string source = "nop # one\nnop // two\n/* three\n   four */ ret";

        Assert.DoesNotContain(TokenKind.Comment, Kinds(source));
        Assert.Equal(3, Kinds(source).Count(k => k == TokenKind.Identifier));

        var comments = Lexer.Tokenize(source, includeComments: true)
            .Where(t => t.Kind == TokenKind.Comment)
            .Select(t => source.Substring(t.Span.Start, t.Span.Length))
            .ToArray();
        Assert.Equal(["# one", "// two", "/* three", "   four */"], comments);
    }

    [Fact]
    public void ABlockCommentKeepsTheLineCountRight()
    {
        var tokens = Lexer.Tokenize("/* a\n b\n c */ ret");
        var ret = tokens.Single(t => t.Kind == TokenKind.Identifier);

        Assert.Equal((3, 7), (ret.Span.Line, ret.Span.Column));
    }

    [Fact]
    public void AnyInputIsTokenizedWithoutFailingAndSpansStayInOrder()
    {
        // The editor runs the lexer on every keystroke, on text that is rarely a valid program.
        const string alphabet = "abcxf019 \t\n\r,():=+-*/%<>&|^~#;'\"\\.$_@!é😀";
        var random = new SeededRandom(0xF37C_1001);
        for (var round = 0; round < 3000; round++)
        {
            var length = random.Next(0, 60);
            var source = string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(0, alphabet.Length - 1)]));

            var tokens = Lexer.Tokenize(source, includeComments: true);

            var end = 0;
            foreach (var token in tokens)
            {
                if (token.Span.Start < end || token.Span.Start + token.Span.Length > source.Length
                    || token.Span.Line < 1 || token.Span.Column < 1)
                {
                    Assert.Fail($"seed {random.Seed:X}, round {round}: bad span {token.Span} in {source}");
                }

                end = token.Span.Start + token.Span.Length;
            }

            Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
        }
    }
}
