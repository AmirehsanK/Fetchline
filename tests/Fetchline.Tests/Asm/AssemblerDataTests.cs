using Fetchline.Core.Asm;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Asm;

/// <summary>Sections, labels, constants and data: everything about a program except its instructions.</summary>
public class AssemblerDataTests
{
    [Fact]
    public void SectionsAreLaidOutAtTheAddressesTheSpecificationGives()
    {
        var program = Assemble("""
            .data
            a:  .word 1, 2
            b:  .byte 3
            .text
                .word 0x11223344
            .rodata
            r:  .asciz "hi"
            .bss
            z:  .zero 64
            """);

        Assert.Equal(0x1000_0000u, program.AddressOf("a"));
        Assert.Equal(0x1000_0008u, program.AddressOf("b"));
        Assert.Equal(0x1000_0010u, program.AddressOf("r"));   // .rodata follows .data, aligned to 16
        Assert.Equal(0x1000_0020u, program.AddressOf("z"));   // .bss follows .rodata, aligned to 16

        Assert.Equal(["text", "data", "rodata", "bss"], program.Segments.Select(s => s.Name));
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, program.Bytes("text"));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3 }, program.Bytes("data"));
        Assert.Equal("hi\0"u8.ToArray(), program.Bytes("rodata"));

        var bss = program.Segment("bss");
        Assert.Equal((64u, 0), (bss.Size, bss.Data.Length));

        Assert.Equal(SegmentFlags.Read | SegmentFlags.Execute, program.Segment("text").Flags);
        Assert.Equal(SegmentFlags.Read | SegmentFlags.Write, program.Segment("data").Flags);
        Assert.Equal(SegmentFlags.Read, program.Segment("rodata").Flags);
        Assert.Equal(0u, program.Entry);
    }

    [Fact]
    public void AnEmptySourceIsAnEmptyProgram()
    {
        var program = Assemble("  # nothing here\n\n");

        var text = Assert.Single(program.Segments);
        Assert.Equal(("text", 0u, 0u), (text.Name, text.Address, text.Size));
        Assert.Empty(program.Symbols);
    }

    [Fact]
    public void NumbersAreStoredLittleEndianAtEveryWidth()
    {
        var program = Assemble("""
            .data
            .byte  1, -1, 255, 'A'
            .half  0x1234, -2
            .2byte 0xFFFF
            .short 1
            .word  0x12345678, -1, 0xFFFFFFFF
            .4byte 1
            .long  2
            .int   3
            .dword 0x1122334455667788
            .quad  -1
            .8byte 1
            """);

        Assert.Equal(
            new byte[]
            {
                1, 0xFF, 0xFF, 0x41,
                0x34, 0x12, 0xFE, 0xFF, 0xFF, 0xFF, 1, 0,
                0x78, 0x56, 0x34, 0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0,
                0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                1, 0, 0, 0, 0, 0, 0, 0,
            },
            program.Bytes("data"));
    }

    [Theory]
    [InlineData(".byte 256", "256 does not fit in 1 byte")]
    [InlineData(".byte -129", "-129 does not fit in 1 byte")]
    [InlineData(".half 65536", "65536 does not fit in 2 bytes")]
    [InlineData(".word 0x100000000", "4294967296 does not fit in 4 bytes")]
    [InlineData(".word -0x80000001", "-2147483649 does not fit in 4 bytes")]
    public void ANumberTooWideForItsDirectiveIsAnError(string line, string message)
    {
        var diagnostic = DiagnoseOne(".data\n" + line);

        Assert.Equal(message, diagnostic.Message);
        Assert.Equal((2, 7), (diagnostic.Span.Line, diagnostic.Span.Column));
    }

    [Fact]
    public void StringsAreStoredAsBytesWithOrWithoutATerminator()
    {
        var program = Assemble("""
            .data
            .ascii  "ab", "c"
            .asciz  "de"
            .string "f\n", ""
            .ascii  "سلام"
            """);

        Assert.Equal("abcde\0f\n\0\0سلام"u8.ToArray(), program.Bytes("data"));
    }

    [Fact]
    public void SpaceIsReservedAndFilled()
    {
        var program = Assemble("""
            .data
            a: .zero 3
            b: .space 2, 0xAA
            c: .skip 1, -1
            d: .byte 7
            e: .zero 0
            f: .byte 8
            """);

        Assert.Equal(new byte[] { 0, 0, 0, 0xAA, 0xAA, 0xFF, 7, 8 }, program.Bytes("data"));
        Assert.Equal(0x1000_0006u, program.AddressOf("d"));
        Assert.Equal(program.AddressOf("e"), program.AddressOf("f"));
    }

    [Fact]
    public void AlignmentPadsToAPowerOfTwo()
    {
        var program = Assemble("""
            .data
                .byte 1
                .align 2
            a:  .byte 2
                .p2align 3
            b:  .byte 3
                .balign 4
            c:  .byte 4
                .align 0
            d:  .byte 5
                .balign 4
                .balign 4
            e:  .byte 6
            """);

        Assert.Equal(0x1000_0004u, program.AddressOf("a"));
        Assert.Equal(0x1000_0008u, program.AddressOf("b"));
        Assert.Equal(0x1000_000Cu, program.AddressOf("c"));
        Assert.Equal(0x1000_000Du, program.AddressOf("d"));
        Assert.Equal(0x1000_0010u, program.AddressOf("e"));   // already aligned: the second .balign adds nothing
        Assert.Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 5, 0, 0, 6 }, program.Bytes("data"));
    }

    [Fact]
    public void PaddingInCodeIsMadeOfNoOpsWhenItCanBe()
    {
        var program = Assemble("""
            .text
                .word 1
                .align 3
                .word 2
                .byte 3
                .align 2
                .word 4
            """);

        Assert.Equal(
            new byte[]
            {
                1, 0, 0, 0,
                0x13, 0, 0, 0,      // addi x0, x0, 0
                2, 0, 0, 0,
                3, 0, 0, 0,         // three bytes cannot hold a no-op, so they are zeros
                4, 0, 0, 0,
            },
            program.Bytes("text"));
    }

    [Fact]
    public void ASectionThatAsksForAStricterAlignmentStartsOnIt()
    {
        var program = Assemble("""
            .data
                .byte 1
            .bss
                .balign 256
            big: .zero 16
            """);

        Assert.Equal(0x1000_0100u, program.AddressOf("big"));
    }

    [Fact]
    public void ConstantsCanBeDefinedThreeWaysAndUsedBeforeTheirDefinition()
    {
        var program = Assemble("""
            .data
                .word FIRST, SECOND, THIRD, LATER
            .equ FIRST, 10
            .set SECOND, FIRST * 2
            THIRD = SECOND + 1
            LATER = AFTER + 1
            AFTER = 99
            """);

        Assert.Equal(new byte[] { 10, 0, 0, 0, 20, 0, 0, 0, 21, 0, 0, 0, 100, 0, 0, 0 }, program.Bytes("data"));

        Assert.True(program.TryGetSymbol("LATER", out var later));
        Assert.Equal((100u, SymbolKind.Constant, null), (later.Value, later.Kind, later.Section));
    }

    [Fact]
    public void TheLengthOfSomethingIsTheDistanceFromItsLabelToHere()
    {
        var program = Assemble("""
            .data
            msg: .ascii "Hello, world\n"
            .equ LEN, . - msg
            tail: .word LEN, end - msg, . - msg
            end:
            """);

        // "." is the address of the statement it is written in, not of the value being stored.
        Assert.Equal(new byte[] { 13, 0, 0, 0, 25, 0, 0, 0, 13, 0, 0, 0 }, program.Bytes("data")[13..]);
    }

    [Fact]
    public void ALabelIsItsAddressInAnExpression()
    {
        var program = Assemble("""
            .data
            a: .word b, a + 4, text_label
            b: .word a
            .text
            text_label: .word b - a
            """);

        Assert.Equal(
            new byte[] { 0x0C, 0, 0, 0x10, 0x04, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0, 0x10 },
            program.Bytes("data"));
        Assert.Equal(new byte[] { 12, 0, 0, 0 }, program.Bytes("text"));
    }

    [Fact]
    public void NumericLocalLabelsReferToTheNearestOneEachWay()
    {
        var program = Assemble("""
            .data
            1:  .word 1b, 1f
            1:  .word 1b, 2f
            2:  .word 1b, 2b
            """);

        static byte[] Word(uint value) => BitConverter.GetBytes(value);
        Assert.Equal(
            [
                .. Word(0x1000_0000), .. Word(0x1000_0008),
                .. Word(0x1000_0008), .. Word(0x1000_0010),
                .. Word(0x1000_0008), .. Word(0x1000_0010),
            ],
            program.Bytes("data"));

        // Local labels are positions, not names: they are not among the program's symbols.
        Assert.Empty(program.Symbols);
    }

    [Fact]
    public void TheEntryPointIsStartWhenItExists()
    {
        Assert.Equal(0u, Assemble(".text\n.word 0\nmain: .word 0").Entry);
        Assert.Equal(8u, Assemble(".text\n.word 0, 0\n_start: .word 0").Entry);
    }

    [Fact]
    public void TheBaseAddressesCanBeChosen()
    {
        var options = new AssemblerOptions { TextBase = 0x8000_0000, DataBase = 0x8000_2000 };
        var program = Assemble(".text\nt: .word d\n.data\nd: .word t", options);

        Assert.Equal(0x8000_0000u, program.AddressOf("t"));
        Assert.Equal(0x8000_2000u, program.AddressOf("d"));
        Assert.Equal(0x8000_0000u, program.Entry);
        Assert.Equal(new byte[] { 0, 0x20, 0, 0x80 }, program.Bytes("text"));
    }

    [Fact]
    public void GlobalSymbolsAreMarked()
    {
        var program = Assemble(".globl main\n.global helper, LIMIT\n.text\nmain:\nhelper:\nlocal:\nLIMIT = 5");

        Assert.Equal(
            [("main", true), ("helper", true), ("local", false), ("LIMIT", true)],
            new[] { "main", "helper", "local", "LIMIT" }.Select(name =>
            {
                Assert.True(program.TryGetSymbol(name, out var symbol));
                return (name, symbol.IsGlobal);
            }));
    }

    [Fact]
    public void SectionNamesFromACompilerMapOntoTheFourSections()
    {
        var program = Assemble("""
            .section .rodata.str1.4,"aMS",@progbits,1
            r: .asciz "x"
            .section .sdata
            d: .word 1
            .section .sbss,"aw",@nobits
            b: .zero 4
            .section .text.startup
            t: .word 0
            """);

        Assert.True(program.TryGetSymbol("r", out var r) && r.Section == ".rodata");
        Assert.True(program.TryGetSymbol("d", out var d) && d.Section == ".data");
        Assert.True(program.TryGetSymbol("b", out var b) && b.Section == ".bss");
        Assert.True(program.TryGetSymbol("t", out var t) && t.Section == ".text");
    }

    [Fact]
    public void AProgramCanBeReadBackWordByWord()
    {
        var program = Assemble(".text\nfirst: .word 0xAABBCCDD\nsecond: .byte 1\n.bss\nz: .zero 8");

        Assert.True(program.TryReadWord(0, out var word));
        Assert.Equal(0xAABBCCDDu, word);
        Assert.False(program.TryReadWord(2, out _));          // runs past the end of the segment
        Assert.False(program.TryReadWord(0x5000, out _));     // nothing is there

        Assert.True(program.TryReadWord(program.AddressOf("z") + 4, out var zero));
        Assert.Equal(0u, zero);                               // .bss reads as zeros

        Assert.Equal("first", program.LabelAt(0));
        Assert.Equal("second", program.LabelAt(4));
        Assert.Null(program.LabelAt(1));
        Assert.Equal("text", program.SegmentAt(3)!.Name);
        Assert.Null(program.SegmentAt(0x9000_0000));
    }

    [Theory]
    [InlineData(".wrod 5", 1, 1, "unknown directive '.wrod'", "did you mean '.word'?")]
    [InlineData(".strng \"x\"", 1, 1, "unknown directive '.strng'", "did you mean '.string'?")]
    [InlineData(".macro foo", 1, 1, "unknown directive '.macro'", null)]
    [InlineData(".section .stack", 1, 10, "unknown section '.stack'", "the sections are .text, .data, .rodata and .bss")]
    [InlineData(".data\n.word", 2, 1, "'.word' needs at least one value", null)]
    [InlineData(".data\n.byte \"x\"", 2, 7, "expected a number", "use .ascii or .asciz for a string")]
    [InlineData(".data\n.word a0", 2, 7, "expected a number", null)]
    [InlineData(".data\n.asciz 5", 2, 8, "'.asciz' takes quoted strings: .asciz \"text\"", null)]
    [InlineData(".data\n.ascii", 2, 1, "'.ascii' takes quoted strings: .ascii \"text\"", null)]
    [InlineData(".data\n.zero", 2, 1, "'.zero' takes a size, and optionally a fill value", null)]
    [InlineData(".data\n.zero -1", 2, 7, "a size cannot be negative (-1)", null)]
    [InlineData(".data\n.space 4, 256", 2, 11, "a fill value is one byte, not 256", null)]
    [InlineData(".data\n.zero later\nlater = 4", 2, 7, "the size must be a number that is known at this point", "it cannot depend on a label defined later, or on an address")]
    [InlineData(".data\nx: .zero x", 2, 10, "the size must be a number that is known at this point", "it cannot depend on a label defined later, or on an address")]
    [InlineData(".align 17", 1, 8, "'.align 17' asks for 2^17 bytes; the exponent is 0 to 16", null)]
    [InlineData(".align 16\n.align 32", 2, 8, "'.align 32' asks for 2^32 bytes; the exponent is 0 to 16", "for 32 bytes write .balign 32")]
    [InlineData(".balign 3", 1, 9, "an alignment is a power of two up to 65536, not 3", null)]
    [InlineData(".balign 0", 1, 9, "an alignment is a power of two up to 65536, not 0", null)]
    [InlineData(".align", 1, 1, "'.align' takes one number", null)]
    [InlineData(".equ SIZE", 1, 1, "'.equ' takes a name and a value: .equ SIZE, 64", null)]
    [InlineData(".equ a0, 5", 1, 6, "a register cannot be a symbol", null)]
    [InlineData(".equ 5, 5", 1, 6, "expected a symbol name", null)]
    [InlineData("sp = 5", 1, 1, "'sp' is a register and cannot be a symbol", null)]
    [InlineData(".globl 5", 1, 8, "expected a symbol name", null)]
    [InlineData(".data\n.word nowhere", 2, 7, "undefined symbol 'nowhere'", null)]
    [InlineData(".data\ncount: .word 1\n.word cuont", 3, 7, "undefined symbol 'cuont'", "did you mean 'count'?")]
    [InlineData(".data\n.word 1f", 2, 7, "there is no label '1:' after this point", null)]
    [InlineData(".data\n.word 3b", 2, 7, "there is no label '3:' before this point", null)]
    [InlineData(".data\n.word 1 / (2 - 2)", 2, 11, "division by zero", null)]
    [InlineData(".bss\n.word 5", 2, 7, ".bss holds no data, only space", "put initialised data in .data, or reserve space with .zero")]
    [InlineData(".bss\n.asciz \"x\"", 2, 1, ".bss holds no data, only space", "put initialised data in .data, or reserve space with .zero")]
    [InlineData(".bss\n.space 4, 1", 2, 1, ".bss can only be filled with zeros", null)]
    [InlineData(".data\n.zero 0x7fffffff", 2, 1, ".data would be larger than 16 MB", null)]
    [InlineData(".bss\n.zero 0x7fffffff", 2, 1, ".bss would be larger than 64 MB", null)]
    public void MistakesAreReportedWithALineAColumnAndOftenAHint(
        string source, int line, int column, string message, string? hint)
    {
        var diagnostic = DiagnoseOne(source);

        Assert.Equal(message, diagnostic.Message);
        Assert.Equal((line, column), (diagnostic.Span.Line, diagnostic.Span.Column));
        Assert.Equal(hint, diagnostic.Hint);
    }

    [Fact]
    public void ASymbolCanOnlyBeDefinedOnce()
    {
        var diagnostic = DiagnoseOne(".data\nx: .word 1\ny: .word 2\nx: .word 3");

        Assert.Equal("'x' is already defined", diagnostic.Message);
        Assert.Equal("it was first defined on line 2", diagnostic.Hint);
        Assert.Equal((4, 1), (diagnostic.Span.Line, diagnostic.Span.Column));

        Assert.Equal("'N' is already defined", DiagnoseOne("N = 1\n.equ N, 2").Message);
        Assert.Equal("'N' is already defined", DiagnoseOne("N = 1\nN: .word 0").Message);
    }

    [Fact]
    public void AConstantDefinedInTermsOfItselfIsAnError()
    {
        Assert.Contains(Diagnose("A = B + 1\nB = A + 1"), d => d.Message == "'A' is defined in terms of itself"
            || d.Message == "'B' is defined in terms of itself");
        Assert.Equal("'X' is defined in terms of itself", DiagnoseOne("X = X").Message);
    }

    [Fact]
    public void ZeroInBssIsAllowedBecauseItIsOnlySpace()
    {
        var program = Assemble(".bss\na: .word 0, 0\nb: .byte 0\n.space 3, 0\nc:");

        Assert.Equal(12u, program.Segment("bss").Size);
        Assert.Equal(program.AddressOf("a") + 12, program.AddressOf("c"));
    }

    [Fact]
    public void SeveralMistakesAreAllReportedInOneRun()
    {
        var diagnostics = Diagnose(".data\n.byte 300\n.wrod 1\n.word missing\n.half 70000");

        Assert.Equal([3, 2, 4, 5], diagnostics.Select(d => d.Span.Line));
    }

    [Fact]
    public void ALabelOnABrokenLineIsStillDefined()
    {
        // The line is wrong, but "x" exists, so using it must not be reported as a second mistake.
        var diagnostic = DiagnoseOne(".data\nx: .word 1 2\n.word x");

        Assert.Equal(2, diagnostic.Span.Line);
    }
}
