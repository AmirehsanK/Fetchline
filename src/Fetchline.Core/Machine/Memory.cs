using System.Buffers.Binary;

namespace Fetchline.Core.Machine;

/// <summary>
/// The whole 32-bit address space, held as 4 KB pages that exist only once something is written
/// to them. Memory that was never written reads as zero. This class stores bytes and nothing
/// else: whether an address may be touched at all is the business of <see cref="MemoryMap"/>.
/// </summary>
public sealed class Memory
{
    public const int PageSize = 4096;
    private const int PageBits = 12;
    private const uint OffsetMask = PageSize - 1;

    private const int CacheSlots = 16;

    private readonly Dictionary<uint, byte[]> _pages = [];

    // A running program keeps going back to the same few pages: its code, its data, its stack.
    // Remembering only the last one would be undone by every load, since the next fetch is from
    // another page, so a handful are remembered, each in the slot its page number hashes to.
    private readonly uint[] _cachedIndex = new uint[CacheSlots];
    private readonly byte[]?[] _cachedPage = new byte[]?[CacheSlots];

    public Memory()
    {
        Array.Fill(_cachedIndex, uint.MaxValue);
    }

    /// <summary>How many pages have been written to.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The addresses of the pages that exist, in order.</summary>
    public IEnumerable<uint> PageAddresses => _pages.Keys.Order().Select(index => index << PageBits);

    public byte ReadByte(uint address) => Page(address) is { } page ? page[address & OffsetMask] : (byte)0;

    public uint ReadU16(uint address)
    {
        var offset = (int)(address & OffsetMask);
        if (offset <= PageSize - 2)
        {
            return Page(address) is { } page ? BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(offset)) : 0u;
        }

        return ReadByte(address) | ((uint)ReadByte(address + 1) << 8);
    }

    public uint ReadU32(uint address)
    {
        var offset = (int)(address & OffsetMask);
        if (offset <= PageSize - 4)
        {
            return Page(address) is { } page ? BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(offset)) : 0u;
        }

        // The word straddles two pages; addresses wrap at the top of memory.
        return ReadByte(address)
            | ((uint)ReadByte(address + 1) << 8)
            | ((uint)ReadByte(address + 2) << 16)
            | ((uint)ReadByte(address + 3) << 24);
    }

    /// <summary>Reads one, two or four bytes, little-endian, without sign extension.</summary>
    public uint Read(uint address, int bytes) => bytes switch
    {
        1 => ReadByte(address),
        2 => ReadU16(address),
        _ => ReadU32(address),
    };

    public void WriteByte(uint address, byte value) => PageForWrite(address)[address & OffsetMask] = value;

    /// <summary>Writes the low one, two or four bytes of a value, little-endian.</summary>
    public void Write(uint address, int bytes, uint value)
    {
        var offset = (int)(address & OffsetMask);
        if (offset <= PageSize - bytes)
        {
            var target = PageForWrite(address).AsSpan(offset);
            switch (bytes)
            {
                case 1:
                    target[0] = (byte)value;
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)value);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(target, value);
                    break;
            }

            return;
        }

        for (var i = 0; i < bytes; i++)
        {
            WriteByte(address + (uint)i, (byte)(value >> (8 * i)));
        }
    }

    /// <summary>Copies bytes in, as when a program is loaded.</summary>
    public void WriteBytes(uint address, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var offset = (int)(address & OffsetMask);
            var count = Math.Min(PageSize - offset, data.Length);
            data[..count].CopyTo(PageForWrite(address).AsSpan(offset));
            data = data[count..];
            address += (uint)count;
        }
    }

    /// <summary>Copies bytes out, as for a memory view or a string a program wants printed.</summary>
    public void ReadBytes(uint address, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var offset = (int)(address & OffsetMask);
            var count = Math.Min(PageSize - offset, destination.Length);
            if (Page(address) is { } page)
            {
                page.AsSpan(offset, count).CopyTo(destination);
            }
            else
            {
                destination[..count].Clear();
            }

            destination = destination[count..];
            address += (uint)count;
        }
    }

    /// <summary>An independent copy, for running the same program on a second machine.</summary>
    public Memory Clone()
    {
        var copy = new Memory();
        foreach (var (index, page) in _pages)
        {
            copy._pages[index] = (byte[])page.Clone();
        }

        return copy;
    }

    // The usual bases (0x0000_0000, 0x1000_0000, 0x7fff_f000) differ only in their high bits,
    // so those are folded in; otherwise code and data would share a slot and evict each other.
    private static int Slot(uint index) => (int)((index ^ (index >> 8) ^ (index >> 16)) & (CacheSlots - 1));

    private byte[]? Page(uint address)
    {
        var index = address >> PageBits;
        var slot = Slot(index);
        if (_cachedIndex[slot] == index)
        {
            return _cachedPage[slot];
        }

        if (!_pages.TryGetValue(index, out var page))
        {
            // A missing page is not remembered: it may come into being on the next write.
            return null;
        }

        _cachedIndex[slot] = index;
        _cachedPage[slot] = page;
        return page;
    }

    private byte[] PageForWrite(uint address)
    {
        if (Page(address) is { } existing)
        {
            return existing;
        }

        var index = address >> PageBits;
        var page = new byte[PageSize];
        _pages[index] = page;
        _cachedIndex[Slot(index)] = index;
        _cachedPage[Slot(index)] = page;
        return page;
    }
}
