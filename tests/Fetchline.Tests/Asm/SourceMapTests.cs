using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Asm;

public class SourceMapTests
{
    private const string Source = """
        # Sum the numbers 1 to 10.
        .data
        total:  .word 0
        .text
        main:
            li   a0, 0              # sum
            li   a1, 10
        loop:
            add  a0, a0, a1
            addi a1, a1, -1
            bnez a1, loop
            la   t0, total
            sw   a0, 0(t0)
        table:
            .word 0xCAFEF00D
            li   a7, 10; ecall
        """;

    [Fact]
    public void EveryInstructionMapsBackToItsLine()
    {
        var program = Assemble(Source);
        var map = program.SourceMap;

        Assert.Equal(
            [
                (0x00u, 6, 0, 1),     // li a0, 0
                (0x04u, 7, 0, 1),     // li a1, 10
                (0x08u, 9, 0, 1),     // add
                (0x0Cu, 10, 0, 1),    // addi
                (0x10u, 11, 0, 1),    // bnez
                (0x14u, 12, 0, 2),    // la: auipc
                (0x18u, 12, 1, 2),    // la: addi
                (0x1Cu, 13, 0, 1),    // sw
                (0x24u, 16, 0, 1),    // li a7, 10
                (0x28u, 16, 0, 1),    // ecall, on the same line
            ],
            map.Entries.Select(e => (e.Address, e.Line, e.Part, e.PartCount)));

        Assert.True(map.TryGetByAddress(0x18, out var entry));
        Assert.Equal("la   t0, total", Source.Substring(entry.Start, entry.Length));
        Assert.Equal(5, entry.Column);

        // 0x20 holds the .word: data, not an instruction, so nothing maps to it.
        Assert.False(map.TryGetByAddress(0x20, out _));
        Assert.False(map.TryGetByAddress(0x02, out _));
        Assert.False(map.TryGetByAddress(0x1000, out _));
    }

    [Fact]
    public void EveryLineMapsForwardToItsInstructions()
    {
        var map = Assemble(Source).SourceMap;

        Assert.Equal([0x14u, 0x18u], map.ByLine(12).Select(e => e.Address));
        Assert.Equal([0x24u, 0x28u], map.ByLine(16).Select(e => e.Address));
        Assert.Empty(map.ByLine(1));     // a comment
        Assert.Empty(map.ByLine(3));     // data
        Assert.Empty(map.ByLine(8));     // a label on its own
        Assert.Empty(map.ByLine(15));    // .word in the text section
        Assert.Empty(map.ByLine(99));
    }

    [Fact]
    public void TheListingShowsTheCodeAndTheSourceWhereTheyDiffer()
    {
        var listing = Listing.Write(Assemble(Source));

        Assert.Equal(
            """
            00000000 <main>:
            00000000:  00000513  li      a0, 0
            00000004:  00a00593  li      a1, 10

            00000008 <loop>:
            00000008:  00b50533  add     a0, a0, a1
            0000000c:  fff58593  addi    a1, a1, -1
            00000010:  fe059ce3  bnez    a1, loop
            00000014:  10000297  auipc   t0, 0x10000      # la t0, total
            00000018:  fec28293  addi    t0, t0, -20
            0000001c:  00a2a023  sw      a0, 0(t0)

            00000020 <table>:
            00000020:  cafef00d  .word   0xcafef00d
            00000024:  00a00893  li      a7, 10
            00000028:  00000073  ecall

            """.ReplaceLineEndings("\n"),
            listing);
    }

    [Fact]
    public void TheListingCanShowTheRealInstructionsAndNumericRegisters()
    {
        var options = DisassemblyOptions.Canonical with { Registers = RegisterStyle.Numeric };
        var listing = Listing.Write(Assemble("main:\n  li a0, 5\n  ret"), options);

        Assert.Equal(
            """
            00000000 <main>:
            00000000:  00500513  addi    x10, x0, 5       # li a0, 5
            00000004:  00008067  jalr    x0, 0(x1)        # ret

            """.ReplaceLineEndings("\n"),
            listing);
    }

    [Fact]
    public void AProgramWithNoCodeHasAnEmptyListing()
    {
        Assert.Equal(string.Empty, Listing.Write(Assemble(".data\n.word 1")));
        Assert.Equal(string.Empty, Listing.Write(Assemble("")));
    }
}
