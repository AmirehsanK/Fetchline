using Fetchline.Core.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Asm;

public class ParserTests
{
    private static List<Statement> Parse(string source)
    {
        var diagnostics = new DiagnosticBag();
        var statements = Parser.Parse(source, diagnostics);
        Assert.Empty(diagnostics.Items);
        Assert.DoesNotContain(statements, s => s.IsBroken);
        return statements;
    }

    private static string Text(string source, SourceSpan span) => source.Substring(span.Start, span.Length);

    [Fact]
    public void AnInstructionHasANameAndOperands()
    {
        const string source = "    addi a0, a1, -5";
        var statement = Assert.Single(Parse(source));

        Assert.Equal(StatementKind.Instruction, statement.Kind);
        Assert.Equal("addi", statement.Name);
        Assert.Equal("addi a0, a1, -5", Text(source, statement.Span));
        Assert.Equal(new RegisterOperand(10, statement.Operands[0].Span), statement.Operands[0]);
        Assert.Equal(new RegisterOperand(11, statement.Operands[1].Span), statement.Operands[1]);
        var immediate = Assert.IsType<ExprOperand>(statement.Operands[2]);
        Assert.Equal(-5, Assert.IsType<NumberExpr>(immediate.Value).Value);
        Assert.Equal("-5", Text(source, immediate.Span));
    }

    [Fact]
    public void MemoryOperandsHaveAnOffsetAndABase()
    {
        var statements = Parse("lw a0, 8(sp)\nsw a1, (a2)\nlw t0, %lo(msg)(t1)\nlw t0, -4 + 2 (s0)\nlw t0, (4)(s0)");

        var plain = Assert.IsType<MemoryOperand>(statements[0].Operands[1]);
        Assert.Equal(2, plain.Base);
        Assert.Equal(8, Assert.IsType<NumberExpr>(plain.Offset).Value);

        var bare = Assert.IsType<MemoryOperand>(statements[1].Operands[1]);
        Assert.Equal(12, bare.Base);
        Assert.Null(bare.Offset);

        var reloc = Assert.IsType<MemoryOperand>(statements[2].Operands[1]);
        Assert.Equal(RelocKind.Lo, Assert.IsType<RelocExpr>(reloc.Offset).Kind);
        Assert.Equal(6, reloc.Base);

        Assert.IsType<BinaryExpr>(Assert.IsType<MemoryOperand>(statements[3].Operands[1]).Offset);
        Assert.Equal(4, Assert.IsType<NumberExpr>(Assert.IsType<MemoryOperand>(statements[4].Operands[1]).Offset).Value);
    }

    [Fact]
    public void LabelsComeBeforeAStatementOrStandAlone()
    {
        var statements = Parse("main:\nloop: inner: addi a0, a0, 1\n1:\n  j 1b\n2: 3: ret");

        Assert.Equal(StatementKind.Empty, statements[0].Kind);
        Assert.Equal("main", Assert.Single(statements[0].Labels).Name);

        Assert.Equal(["loop", "inner"], statements[1].Labels.Select(l => l.Name));
        Assert.Equal("addi", statements[1].Name);
        Assert.All(statements[1].Labels, label => Assert.False(label.IsLocal));

        var local = Assert.Single(statements[2].Labels);
        Assert.True(local.IsLocal);
        Assert.Equal(1, local.LocalNumber);

        var jump = Assert.IsType<ExprOperand>(Assert.Single(statements[3].Operands));
        Assert.Equal(new LocalRefExpr(1, false, jump.Span), jump.Value);

        Assert.Equal([2, 3], statements[4].Labels.Select(l => l.LocalNumber));
    }

    [Fact]
    public void DirectivesAndMnemonicsAreLowerCasedButSymbolsAreNot()
    {
        var statements = Parse(".TEXT\nADDI a0, a0, 1\n.Word Value\nFence.I");

        Assert.Equal((StatementKind.Directive, ".text"), (statements[0].Kind, statements[0].Name));
        Assert.Equal((StatementKind.Instruction, "addi"), (statements[1].Kind, statements[1].Name));
        Assert.Equal(".word", statements[2].Name);
        var symbol = Assert.IsType<ExprOperand>(statements[2].Operands[0]);
        Assert.Equal("Value", Assert.IsType<SymbolExpr>(symbol.Value).Name);
        Assert.Equal("fence.i", statements[3].Name);
    }

    [Fact]
    public void DirectivesTakeExpressionsAndStrings()
    {
        var statements = Parse(".word 1, 2 + 3, end - start\n.asciz \"hi\", \"there\"\n.equ SIZE, 4 * 16\n.globl main, helper");

        Assert.Equal(3, statements[0].Operands.Count);
        Assert.All(statements[1].Operands, operand => Assert.IsType<StringOperand>(operand));
        Assert.Equal("hi"u8.ToArray(), ((StringOperand)statements[1].Operands[0]).Bytes);
        Assert.Equal(2, statements[2].Operands.Count);
        Assert.Equal(2, statements[3].Operands.Count);
    }

    [Fact]
    public void AnAssignmentDefinesASymbol()
    {
        var statement = Assert.Single(Parse("LIMIT = 10 * 4"));

        Assert.Equal(StatementKind.Assignment, statement.Kind);
        Assert.Equal("LIMIT", statement.Name);
        Assert.IsType<BinaryExpr>(Assert.IsType<ExprOperand>(Assert.Single(statement.Operands)).Value);
    }

    [Fact]
    public void WhatALinkerWouldReadIsAcceptedAndSkipped()
    {
        // The output of a C compiler is full of these; none of them changes the program here.
        var statements = Parse(
            ".file \"main.c\"\n.option nopic\n.attribute arch, \"rv32i2p1_m2p0\"\n" +
            ".type main, @function\n.size main, .-main\n.ident \"GCC: 13.2.0\"\n" +
            ".section .rodata.str1.4,\"aMS\",@progbits,1\n.section .text");

        Assert.All(statements, s => Assert.Equal(StatementKind.Directive, s.Kind));
        Assert.Empty(statements[0].Operands);
        var section = Assert.IsType<ExprOperand>(Assert.Single(statements[6].Operands));
        Assert.Equal(".rodata.str1.4", Assert.IsType<SymbolExpr>(section.Value).Name);
        Assert.Equal(".text", ((SymbolExpr)((ExprOperand)statements[7].Operands[0]).Value).Name);
    }

    [Fact]
    public void SeveralStatementsMayShareALine()
    {
        var statements = Parse("li a0, 1; li a1, 2 ; ecall");

        Assert.Equal(["li", "li", "ecall"], statements.Select(s => s.Name));
        Assert.Empty(statements[2].Operands);
    }

    [Theory]
    [InlineData("addi a0, a1,", 1, 13, "expected an operand after ','")]
    [InlineData("addi a0 a1", 1, 9, "expected ',' or the end of the line")]
    [InlineData("lw a0, 8(sp", 1, 12, "expected ')'")]
    [InlineData("lw a0, 8(sx)", 1, 10, "expected a base register")]
    [InlineData("lw a0, 8()", 1, 10, "expected a base register")]
    [InlineData("lw a0, 8(", 1, 10, "expected a base register")]
    [InlineData("addi a0, a1, (1 + ", 1, 18, "expected a number, a symbol or '('")]
    [InlineData("nop\n  , a0", 2, 3, "expected a label, an instruction or a directive")]
    [InlineData("42", 1, 1, "expected a label, an instruction or a directive")]
    [InlineData("a0: nop", 1, 1, "'a0' is a register and cannot be a label")]
    [InlineData("x = ", 1, 4, "expected a number, a symbol or '('")]
    [InlineData("x = 1 2", 1, 7, "expected ',' or the end of the line")]
    [InlineData(".section", 1, 9, "expected a section name")]
    [InlineData("li a0, 08", 1, 8, "'08' is not a number")]
    [InlineData("li a0, 5 @", 1, 10, "unexpected character '@'")]
    [InlineData("@", 1, 1, "unexpected character '@'")]
    [InlineData(".asciz \"open", 1, 8, "this string is never closed")]
    public void AMistakeIsReportedOnceWithItsLineAndColumn(string source, int line, int column, string message)
    {
        var diagnostics = new DiagnosticBag();
        var statements = Parser.Parse(source, diagnostics);

        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal((line, column), (diagnostic.Span.Line, diagnostic.Span.Column));
        Assert.Contains(statements, s => s.IsBroken);
    }

    [Fact]
    public void ABaseRegisterThatIsNearlyRightGetsASuggestion()
    {
        var diagnostics = new DiagnosticBag();
        Parser.Parse("lw a0, 8(spp)", diagnostics);

        Assert.Equal("did you mean 'sp'?", Assert.Single(diagnostics.Items).Hint);
    }

    [Fact]
    public void ABrokenLineDoesNotStopTheLinesAfterItFromBeingChecked()
    {
        var diagnostics = new DiagnosticBag();
        var statements = Parser.Parse("addi a0 a1\nnop\nlw a0, 8(\nret", diagnostics);

        Assert.Equal(2, diagnostics.Items.Count);
        Assert.Equal([1, 3], diagnostics.Items.Select(d => d.Span.Line));
        Assert.Equal([true, false, true, false], statements.Select(s => s.IsBroken));
        Assert.Equal(["addi", "nop", "lw", "ret"], statements.Select(s => s.Name));
    }

    [Fact]
    public void AnyInputParsesWithoutFailing()
    {
        // Random text built from the pieces of real programs, so most lines are nearly valid.
        string[] pieces =
        [
            "addi", "lw", "sw", "beq", "li", ".word", ".asciz", ".section", "a0", "sp", "x31", "loop", "1", "1b",
            "0x10", "(", ")", ",", ":", "=", "+", "-", "*", "%lo", "%hi", "\"s\"", "'c'", "#", ";", "\n", "\n", " ", " ",
        ];
        var random = new SeededRandom(0xF37C_2301);
        for (var round = 0; round < 4000; round++)
        {
            var source = string.Concat(Enumerable.Range(0, random.Next(0, 25)).Select(_ => random.Pick(pieces) + " "));
            var diagnostics = new DiagnosticBag();

            var statements = Parser.Parse(source, diagnostics);

            Assert.Equal(statements.Any(s => s.IsBroken), diagnostics.HasErrors);
            foreach (var diagnostic in diagnostics.Items)
            {
                if (diagnostic.Span.Line < 1 || diagnostic.Span.Column < 1
                    || diagnostic.Span.Start + diagnostic.Span.Length > source.Length)
                {
                    Assert.Fail($"seed {random.Seed:X}, round {round}: diagnostic outside the source: {diagnostic} in {source}");
                }

                diagnostic.Render(source);
            }
        }
    }
}
