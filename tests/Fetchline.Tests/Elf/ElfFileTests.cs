using System.Buffers.Binary;
using Fetchline.Core.Asm;
using Fetchline.Core.Elf;
using Fetchline.Core.Isa;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Elf;

public class ElfFileTests
{
    private const string Source = """
        .globl _start, counter
        .equ LIMIT, 10
        .data
        counter: .word 0
        message: .asciz "done\n"
        .rodata
        table:   .word 1, 2, 3
        .bss
        scratch: .zero 64
        .text
        helper:
            ret
        _start:
            la   t0, counter
            li   t1, LIMIT
            call helper
        """;

    public static TheoryData<string> OfficialTests() => Vectors.Rows();

    [Fact]
    public void AProgramSurvivesBeingWrittenAndReadBack()
    {
        var original = Assemble(Source);

        var restored = ElfFile.Read(ElfFile.Write(original));

        Assert.Equal(original.Entry, restored.Entry);
        Assert.Equal(4u, restored.Entry);
        Assert.Equal(original.Segments.Count, restored.Segments.Count);
        foreach (var (before, after) in original.Segments.Zip(restored.Segments))
        {
            Assert.Equal((before.Name, before.Address, before.Size, before.Flags), (after.Name, after.Address, after.Size, after.Flags));
            Assert.Equal(before.Data.ToArray(), after.Data.ToArray());
        }

        Assert.Equal(original.Symbols, restored.Symbols);
        Assert.True(restored.TryGetSymbol("LIMIT", out var limit));
        Assert.Equal((10u, SymbolKind.Constant, null), (limit.Value, limit.Kind, limit.Section));
        Assert.True(restored.TryGetSymbol("counter", out var counter));
        Assert.Equal((true, ".data"), (counter.IsGlobal, counter.Section));
        Assert.True(restored.TryGetSymbol("helper", out var helper));
        Assert.Equal((false, ".text"), (helper.IsGlobal, helper.Section));

        // The source and its map belong to the assembly, not to the file.
        Assert.Null(restored.Source);
        Assert.Empty(restored.SourceMap.Entries);
    }

    [Fact]
    public void TheHeaderSaysWhatKindOfFileItIs()
    {
        var file = ElfFile.Write(Assemble(Source));

        Assert.True(ElfFile.LooksLikeElf(file));
        Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1 }, file[..7]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(16)));     // executable
        Assert.Equal(243, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(18)));   // RISC-V
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));    // entry
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(44)));     // four loadable segments
    }

    [Fact]
    public void EachSegmentSitsAtAFileOffsetCongruentToItsAddress()
    {
        // A loader that maps whole pages needs offset and address to agree modulo the page size.
        var file = ElfFile.Write(Assemble(Source));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(44));

        for (var i = 0; i < count; i++)
        {
            var header = file.AsSpan(52 + (32 * i));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var address = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            if (fileSize > 0)
            {
                Assert.Equal(address % 0x1000, offset % 0x1000);
            }
        }
    }

    [Fact]
    public void AnEmptyProgramIsStillAValidFile()
    {
        var restored = ElfFile.Read(ElfFile.Write(Assemble("")));

        Assert.Empty(restored.Segments);
        Assert.Empty(restored.Symbols);
        Assert.Equal(0u, restored.Entry);
    }

    [Theory]
    [MemberData(nameof(OfficialTests))]
    public void AnOfficialTestProgramIsReadWithItsSymbols(string name)
    {
        var program = ElfFile.Read(Vectors.Elf(name));

        // Every test starts at the reset address with a jump over its trap vector, and has the
        // word the test's verdict is written to.
        Assert.Equal(0x8000_0000u, program.Entry);
        Assert.NotNull(program.Text);
        Assert.Equal(0x8000_0000u, program.Text.Address);
        Assert.True(program.TryReadWord(program.Entry, out var first));
        Assert.True(program.TryGetSymbol("reset_vector", out var reset));
        var jump = Decoder.Decode(first);
        Assert.Equal((Op.Jal, 0), (jump.Op, jump.Rd));
        Assert.Equal(reset.Value, program.Entry + (uint)jump.Imm);

        Assert.True(program.TryGetSymbol("tohost", out var tohost));
        Assert.Equal(SymbolKind.Label, tohost.Kind);
        Assert.NotNull(program.SegmentAt(tohost.Value));
        Assert.True(program.TryGetSymbol("_start", out var start));
        Assert.Equal(program.Entry, start.Value);
        Assert.Equal("_start", program.LabelAt(program.Entry));
    }

    [Fact]
    public void AllSixtySixOfficialTestsAreHere()
    {
        var names = Vectors.Names;

        Assert.Equal(42, names.Count(n => n.StartsWith("rv32ui-p-", StringComparison.Ordinal)));
        Assert.Equal(8, names.Count(n => n.StartsWith("rv32um-p-", StringComparison.Ordinal)));
        Assert.Equal(16, names.Count(n => n.StartsWith("rv32mi-p-", StringComparison.Ordinal)));
        Assert.Equal(66, names.Count);
    }

    [Fact]
    public void AFileThatIsNotAnExecutableForThisMachineIsRefusedWithAReason()
    {
        var good = ElfFile.Write(Assemble(Source));

        static string Reason(byte[] file)
        {
            Assert.False(ElfFile.TryRead(file, out var program, out var error));
            Assert.Null(program);
            Assert.Throws<FormatException>(() => ElfFile.Read(file));
            return error!;
        }

        static byte[] With(byte[] file, int at, byte value)
        {
            var copy = (byte[])file.Clone();
            copy[at] = value;
            return copy;
        }

        Assert.Equal("not an ELF file", Reason([]));
        Assert.Equal("not an ELF file", Reason("    li a0, 5\n"u8.ToArray()));
        Assert.Equal("the file ends inside the ELF header", Reason(good[..20]));
        Assert.Equal("a 64-bit ELF file; this core is RV32", Reason(With(good, 4, 2)));
        Assert.Equal("a big-endian ELF file; RISC-V is little-endian", Reason(With(good, 5, 2)));
        Assert.Equal("an ELF file for another machine (type 62); RISC-V is 243", Reason(With(good, 18, 62)));
        Assert.Equal("not an executable: an object file must be linked first", Reason(With(good, 16, 1)));
        Assert.Equal("the program headers lie outside the file", Reason(good[..60]));
        Assert.False(ElfFile.LooksLikeElf("\x7F"u8 + "EL"u8));
    }

    [Fact]
    public void ADamagedFileNeverMakesTheReaderFail()
    {
        // Whatever is done to the bytes, reading gives a program or a reason, never an exception.
        var good = ElfFile.Write(Assemble(Source));
        var official = Vectors.Elf("rv32ui-p-add");
        var random = new SeededRandom(0xF37C_2801);

        for (var round = 0; round < 6000; round++)
        {
            var file = (byte[])(round % 2 == 0 ? good : official).Clone();
            var edits = random.Next(1, 8);
            for (var i = 0; i < edits; i++)
            {
                // Most damage is aimed at the headers, where the offsets and sizes are.
                var at = random.Chance(70) ? random.Next(0, Math.Min(file.Length, 400) - 1) : random.Next(0, file.Length - 1);
                file[at] = random.Chance(30) ? (byte)0xFF : (byte)random.Next(0, 255);
            }

            if (random.Chance(25))
            {
                file = file[..random.Next(0, file.Length)];
            }

            try
            {
                if (ElfFile.TryRead(file, out var program, out var error))
                {
                    Assert.NotNull(program);
                    Assert.Null(error);
                }
                else
                {
                    Assert.False(string.IsNullOrEmpty(error));
                }
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: {exception}");
            }
        }
    }
}
