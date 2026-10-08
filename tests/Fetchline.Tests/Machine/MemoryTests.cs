using Fetchline.Core.Machine;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Machine;

public class MemoryTests
{
    [Fact]
    public void MemoryThatWasNeverWrittenReadsAsZeroAndCostsNothing()
    {
        var memory = new Memory();

        Assert.Equal(0u, memory.ReadU32(0));
        Assert.Equal(0u, memory.ReadU32(0xFFFF_FFFC));
        Assert.Equal(0u, memory.Read(0x1234_5678, 2));
        Assert.Equal(0, memory.PageCount);
    }

    [Fact]
    public void ValuesAreStoredLittleEndianAtEveryWidth()
    {
        var memory = new Memory();
        memory.Write(0x100, 4, 0x1122_3344);
        memory.Write(0x104, 2, 0xAABB_CCDD);   // only the low two bytes are stored
        memory.Write(0x106, 1, 0xEE);

        Assert.Equal(0x1122_3344u, memory.ReadU32(0x100));
        Assert.Equal(0x44, memory.ReadByte(0x100));
        Assert.Equal(0x11, memory.ReadByte(0x103));
        Assert.Equal(0x3344u, memory.ReadU16(0x100));
        Assert.Equal(0x2233u, memory.ReadU16(0x101));
        Assert.Equal(0xCCDDu, memory.Read(0x104, 2));
        Assert.Equal(0xEEu, memory.Read(0x106, 1));
        Assert.Equal(0x00EE_CCDDu, memory.Read(0x104, 4));
        Assert.Equal(1, memory.PageCount);
    }

    [Theory]
    [InlineData(0x0000_0FFDu)]   // a word across the first page boundary
    [InlineData(0x0000_0FFFu)]
    [InlineData(0x1000_1FFEu)]
    [InlineData(0xFFFF_FFFEu)]   // across the top of the address space, back to address zero
    public void AnAccessMayStraddleTwoPages(uint address)
    {
        var memory = new Memory();

        memory.Write(address, 4, 0xDEAD_BEEF);

        Assert.Equal(0xDEAD_BEEFu, memory.ReadU32(address));
        Assert.Equal(0xEFu, memory.Read(address, 1));
        Assert.Equal(0xBEEFu, memory.Read(address, 2));
        Assert.Equal(0xDEu, memory.Read(address + 3, 1));
        Assert.Equal(0xDEADu, memory.Read(address + 2, 2));
        Assert.Equal(2, memory.PageCount);
    }

    [Fact]
    public void BlocksAreCopiedInAndOutAcrossPages()
    {
        var memory = new Memory();
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 7)).ToArray();

        memory.WriteBytes(0x0FF0, data);

        var back = new byte[10_000];
        memory.ReadBytes(0x0FF0, back);
        Assert.Equal(data, back);
        Assert.Equal(4, memory.PageCount);
        Assert.Equal([0x0000u, 0x1000u, 0x2000u, 0x3000u], memory.PageAddresses);

        // Reading past what was written gives zeros, without creating pages.
        var beyond = new byte[32];
        Array.Fill(beyond, (byte)0xFF);
        memory.ReadBytes(0x9000_0000 - 16, beyond);
        Assert.All(beyond, b => Assert.Equal(0, b));
        Assert.Equal(4, memory.PageCount);
    }

    [Fact]
    public void ACopyIsIndependentOfTheOriginal()
    {
        var memory = new Memory();
        memory.Write(0x2000, 4, 1);
        var copy = memory.Clone();

        copy.Write(0x2000, 4, 2);
        memory.Write(0x5000, 4, 3);

        Assert.Equal(1u, memory.ReadU32(0x2000));
        Assert.Equal(2u, copy.ReadU32(0x2000));
        Assert.Equal(0u, copy.ReadU32(0x5000));
    }

    [Fact]
    public void RandomReadsAndWritesAgreeWithAPlainArray()
    {
        // A model test: the paged memory must behave like one flat array, wherever the page
        // boundaries fall. The region covers three pages so that many accesses straddle.
        const uint start = 0x7FFF_F000;
        const int size = 3 * Memory.PageSize;
        var memory = new Memory();
        var model = new byte[size];
        var random = new SeededRandom(0xF37C_3101);

        for (var i = 0; i < 200_000; i++)
        {
            var bytes = 1 << random.Next(0, 2);
            var offset = random.Next(0, size - bytes);
            if (random.NextBool())
            {
                var value = random.NextUInt32();
                memory.Write(start + (uint)offset, bytes, value);
                for (var b = 0; b < bytes; b++)
                {
                    model[offset + b] = (byte)(value >> (8 * b));
                }
            }
            else
            {
                uint expected = 0;
                for (var b = 0; b < bytes; b++)
                {
                    expected |= (uint)model[offset + b] << (8 * b);
                }

                if (memory.Read(start + (uint)offset, bytes) != expected)
                {
                    Assert.Fail($"seed {random.Seed:X}: {bytes} bytes at offset {offset:x} differ at step {i}");
                }
            }
        }
    }

    [Fact]
    public void AProgramMayReadItsCodeButNotWriteIt()
    {
        var map = MemoryMap.ForHost(Assemble("main: .word 1, 2, 3\n.data\nv: .word 5"));

        Assert.True(map.Allows(0, 4, Access.Execute));
        Assert.True(map.Allows(8, 4, Access.Read));
        Assert.False(map.Allows(8, 4, Access.Write));
        Assert.False(map.Allows(12, 4, Access.Execute));   // just past the last word
        Assert.False(map.Allows(10, 4, Access.Read));      // the last two bytes are outside
        Assert.Equal("a store to 0x00000008, which is in 'text' and cannot be written", map.Explain(8, 4, Access.Write));
        Assert.Equal("a load from 0x0000000d, which is outside the memory this program has", map.Explain(10, 4, Access.Read));
        Assert.Equal("a jump to 0x0000000c, which is outside the program", map.Explain(12, 4, Access.Execute));
    }

    [Fact]
    public void TheDataAreaIsOpenForAHeapButReadOnlyDataStaysReadOnly()
    {
        var program = Assemble(".data\nv: .word 5\n.rodata\nk: .word 7\n.bss\nz: .zero 8");
        var map = MemoryMap.ForHost(program);
        var constant = program.AddressOf("k");

        Assert.True(map.Allows(program.AddressOf("v"), 4, Access.Write));
        Assert.True(map.Allows(program.AddressOf("z"), 4, Access.Write));
        Assert.True(map.Allows(constant, 4, Access.Read));
        Assert.False(map.Allows(constant, 4, Access.Write));
        Assert.False(map.Allows(constant - 2, 4, Access.Write));   // runs into the read-only word
        Assert.Equal(
            "a store to 0x10000010, which is in 'rodata' and cannot be written",
            map.Explain(constant, 4, Access.Write));

        // Past the program's own data there is a heap: 256 MB from the start of the data.
        Assert.True(map.Allows(0x1000_4000, 4, Access.Write));
        Assert.True(map.Allows(0x1FFF_FFFC, 4, Access.Write));
        Assert.False(map.Allows(0x1FFF_FFFD, 4, Access.Write));
        Assert.False(map.Allows(0x2000_0000, 1, Access.Read));
        Assert.False(map.Allows(0x1000_4000, 4, Access.Execute));
        Assert.Equal(
            "a jump to 0x10004000, which is in 'data and heap' and is not code",
            map.Explain(0x1000_4000, 4, Access.Execute));
    }

    [Fact]
    public void TheStackIsOneMegabyteBelowItsTop()
    {
        var map = MemoryMap.ForHost(Assemble("nop"));

        Assert.True(map.Allows(MemoryMap.StackTop, 4, Access.Write));
        Assert.True(map.Allows(MemoryMap.StackTop - 4, 4, Access.Read));
        Assert.True(map.Allows(MemoryMap.StackBase, 4, Access.Write));
        Assert.False(map.Allows(MemoryMap.StackBase - 4, 4, Access.Write));   // stack overflow
        Assert.True(map.Allows(0x7FFF_FFFC, 4, Access.Write));
        Assert.False(map.Allows(0x7FFF_FFFD, 4, Access.Write));               // would wrap into 0x80000000
        Assert.False(map.Allows(0x8000_0000, 4, Access.Read));
        Assert.Equal(
            "a store to 0x7feffffc, which is outside the memory this program has",
            map.Explain(MemoryMap.StackBase - 4, 4, Access.Write));

        // With no data segment of its own the program still has the usual data area.
        Assert.True(map.Allows(MemoryMap.DataBase, 4, Access.Write));
    }
}
