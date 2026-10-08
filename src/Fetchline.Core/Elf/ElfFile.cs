using System.Buffers.Binary;
using System.Text;
using Fetchline.Core.Asm;

namespace Fetchline.Core.Elf;

/// <summary>
/// Reads and writes 32-bit little-endian RISC-V executables. Only what a program needs is kept:
/// the loadable segments, the symbols and the entry point. Reading never throws on a damaged
/// file; it says what is wrong with it.
/// </summary>
public static class ElfFile
{
    private const int HeaderSize = 52;
    private const int ProgramHeaderSize = 32;
    private const int SectionHeaderSize = 40;
    private const int SymbolSize = 16;

    private const ushort TypeExecutable = 2;
    private const ushort MachineRiscV = 243;
    private const uint SegmentLoad = 1;
    private const uint SectionProgBits = 1;
    private const uint SectionSymbolTable = 2;
    private const uint SectionStringTable = 3;
    private const uint SectionNoBits = 8;
    private const ushort SectionAbsolute = 0xFFF1;
    private const ushort SectionReserved = 0xFF00;
    private const uint PageSize = 0x1000;

    // A segment or a symbol table larger than this is a damaged or hostile file, not a program.
    private const uint MaxSegmentSize = 256 * 1024 * 1024;
    private const int MaxSymbols = 1_000_000;

    /// <summary>Whether the bytes start like an ELF file, to tell one from assembly source.</summary>
    public static bool LooksLikeElf(ReadOnlySpan<byte> file) => file.Length >= 4 && file[..4].SequenceEqual("\x7F"u8 + "ELF"u8);

    /// <summary>Reads an executable, or throws <see cref="FormatException"/> saying why it cannot.</summary>
    public static Program Read(ReadOnlySpan<byte> file) =>
        TryRead(file, out var program, out var error) ? program! : throw new FormatException(error);

    public static bool TryRead(ReadOnlySpan<byte> file, out Program? program, out string? error)
    {
        program = null;
        error = Check(file);
        if (error is not null)
        {
            return false;
        }

        var entry = U32(file, 24);
        var programHeaders = U32(file, 28);
        var sectionHeaders = U32(file, 32);
        var programHeaderSize = U16(file, 42);
        var programHeaderCount = U16(file, 44);
        var sectionHeaderSize = U16(file, 46);
        var sectionCount = U16(file, 48);
        var nameTableIndex = U16(file, 50);

        if (programHeaderCount > 0 && (programHeaderSize < ProgramHeaderSize
            || !Fits(file, programHeaders, (ulong)programHeaderSize * programHeaderCount)))
        {
            error = "the program headers lie outside the file";
            return false;
        }

        // Section headers are optional for running a program. A file whose section table is
        // damaged still loads; it just has no symbols and no section names.
        var sections = new List<SectionHeader>();
        if (sectionCount > 0 && sectionHeaderSize >= SectionHeaderSize
            && Fits(file, sectionHeaders, (ulong)sectionHeaderSize * sectionCount))
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var at = (int)sectionHeaders + (i * sectionHeaderSize);
                sections.Add(new SectionHeader(
                    U32(file, at), U32(file, at + 4), U32(file, at + 12), U32(file, at + 16),
                    U32(file, at + 20), U32(file, at + 24), U32(file, at + 36)));
            }
        }

        var names = nameTableIndex < sections.Count ? sections[nameTableIndex] : default;

        var segments = new List<Segment>();
        for (var i = 0; i < programHeaderCount; i++)
        {
            var at = (int)programHeaders + (i * programHeaderSize);
            if (U32(file, at) != SegmentLoad)
            {
                continue;
            }

            var (offset, address) = (U32(file, at + 4), U32(file, at + 8));
            var (fileSize, memorySize, flags) = (U32(file, at + 16), U32(file, at + 20), U32(file, at + 24));
            if (memorySize == 0)
            {
                continue;
            }

            if (fileSize > memorySize || memorySize > MaxSegmentSize || !Fits(file, offset, fileSize))
            {
                error = $"segment {i} lies outside the file or is larger than {MaxSegmentSize / (1024 * 1024)} MB";
                return false;
            }

            if ((ulong)address + memorySize > 0x1_0000_0000)
            {
                error = $"segment {i} runs past the end of the address space";
                return false;
            }

            var segmentFlags = ((flags & 4) != 0 ? SegmentFlags.Read : 0)
                | ((flags & 2) != 0 ? SegmentFlags.Write : 0)
                | ((flags & 1) != 0 ? SegmentFlags.Execute : 0);

            // A segment is named after the section that starts it, when there is one.
            var section = sections.FindIndex(s => s.Address == address && s.Type is SectionProgBits or SectionNoBits);
            var name = SectionName(file, sections, names, section).TrimStart('.');
            if (name.Length == 0 || segments.Any(s => s.Name == name))
            {
                name = "load" + i;
            }

            segments.Add(new Segment(
                name, address, file.Slice((int)offset, (int)fileSize).ToArray(), memorySize, segmentFlags));
        }

        var symbols = new List<Symbol>();
        var table = sections.FirstOrDefault(s => s.Type == SectionSymbolTable);
        if (table.Type == SectionSymbolTable && table.Link < sections.Count && Fits(file, table.Offset, table.Size))
        {
            var strings = sections[(int)table.Link];
            var count = (int)Math.Min(table.Size / SymbolSize, MaxSymbols);
            for (var i = 1; i < count; i++)
            {
                var at = (int)table.Offset + (i * SymbolSize);
                var (info, sectionIndex) = (file[at + 12], U16(file, at + 14));
                var type = info & 0xF;
                var name = String(file, strings, U32(file, at));

                // Symbols for sections and source files are bookkeeping for a linker.
                if (name.Length == 0 || type is 3 or 4)
                {
                    continue;
                }

                var absolute = sectionIndex == SectionAbsolute;
                symbols.Add(new Symbol(
                    name,
                    U32(file, at + 4),
                    absolute ? SymbolKind.Constant : SymbolKind.Label,
                    IsGlobal: (info >> 4) is 1 or 2,
                    absolute || sectionIndex >= SectionReserved ? null : NullIfEmpty(SectionName(file, sections, names, sectionIndex))));
            }
        }

        program = new Program(segments, symbols, entry);
        return true;
    }

    /// <summary>Writes a program as an executable that the usual RISC-V tools can read.</summary>
    public static byte[] Write(Program program)
    {
        var segments = program.Segments.Where(segment => segment.Size > 0).ToList();

        // Layout: header, program headers, each segment's bytes at a file offset congruent to its
        // address modulo the page size (as a loader that maps pages expects), then the symbol
        // table, its strings, the section names and the section headers.
        var offsets = new uint[segments.Count];
        var position = (uint)(HeaderSize + (ProgramHeaderSize * segments.Count));
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Data.Length > 0)
            {
                position = AlignUp(position, PageSize) + (segments[i].Address & (PageSize - 1));
            }

            offsets[i] = position;
            position += (uint)segments[i].Data.Length;
        }

        var sectionNames = new StringTable();
        var symbolNames = new StringTable();
        var sectionIndexOf = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (var i = 0; i < segments.Count; i++)
        {
            sectionIndexOf["." + segments[i].Name] = (ushort)(i + 1);
        }

        // Local symbols must come before global ones; the table's header says where they end.
        var ordered = program.Symbols.OrderBy(symbol => symbol.IsGlobal).ToList();
        var symbolTable = new byte[SymbolSize * (ordered.Count + 1)];
        for (var i = 0; i < ordered.Count; i++)
        {
            var symbol = ordered[i];
            var entry = symbolTable.AsSpan(SymbolSize * (i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, symbolNames.Add(symbol.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], symbol.Value);
            entry[12] = (byte)(symbol.IsGlobal ? 0x10 : 0x00);
            var index = symbol.Kind == SymbolKind.Constant
                ? SectionAbsolute
                : symbol.Section is not null && sectionIndexOf.TryGetValue(symbol.Section, out var found) ? found : SectionAbsolute;
            BinaryPrimitives.WriteUInt16LittleEndian(entry[14..], index);
        }

        var symbolTableOffset = AlignUp(position, 4);
        var symbolNamesOffset = symbolTableOffset + (uint)symbolTable.Length;
        var symbolTableIndex = segments.Count + 1;

        var headers = new List<SectionHeader> { default };
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var flags = 2u | (segment.IsWritable ? 1u : 0u) | (segment.IsExecutable ? 4u : 0u);
            headers.Add(new SectionHeader(
                sectionNames.Add("." + segment.Name),
                segment.Data.Length > 0 ? SectionProgBits : SectionNoBits,
                segment.Address, offsets[i], segment.Data.Length > 0 ? (uint)segment.Data.Length : segment.Size,
                0, 0, flags, segment.IsExecutable ? 4u : 1u));
        }

        headers.Add(new SectionHeader(
            sectionNames.Add(".symtab"), SectionSymbolTable, 0, symbolTableOffset, (uint)symbolTable.Length,
            (uint)(symbolTableIndex + 1), SymbolSize, 0, 4, (uint)(ordered.Count(symbol => !symbol.IsGlobal) + 1)));
        headers.Add(new SectionHeader(
            sectionNames.Add(".strtab"), SectionStringTable, 0, symbolNamesOffset, (uint)symbolNames.Length, 0, 0, 0, 1));

        var sectionNamesOffset = symbolNamesOffset + (uint)symbolNames.Length;
        var sectionNamesHeader = headers.Count;
        var sectionNamesName = sectionNames.Add(".shstrtab");
        headers.Add(new SectionHeader(
            sectionNamesName, SectionStringTable, 0, sectionNamesOffset, (uint)sectionNames.Length, 0, 0, 0, 1));

        var sectionHeadersOffset = AlignUp(sectionNamesOffset + (uint)sectionNames.Length, 4);
        var file = new byte[sectionHeadersOffset + (uint)(SectionHeaderSize * headers.Count)];

        // ---- ELF header ----
        "\x7F"u8.CopyTo(file);
        "ELF"u8.CopyTo(file.AsSpan(1));
        file[4] = 1;    // 32-bit
        file[5] = 1;    // little-endian
        file[6] = 1;    // ELF version
        Put16(file, 16, TypeExecutable);
        Put16(file, 18, MachineRiscV);
        Put32(file, 20, 1);
        Put32(file, 24, program.Entry);
        Put32(file, 28, HeaderSize);
        Put32(file, 32, sectionHeadersOffset);
        Put16(file, 40, HeaderSize);
        Put16(file, 42, ProgramHeaderSize);
        Put16(file, 44, (ushort)segments.Count);
        Put16(file, 46, SectionHeaderSize);
        Put16(file, 48, (ushort)headers.Count);
        Put16(file, 50, (ushort)sectionNamesHeader);

        // ---- Program headers and segment bytes ----
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var at = HeaderSize + (ProgramHeaderSize * i);
            Put32(file, at, SegmentLoad);
            Put32(file, at + 4, offsets[i]);
            Put32(file, at + 8, segment.Address);
            Put32(file, at + 12, segment.Address);
            Put32(file, at + 16, (uint)segment.Data.Length);
            Put32(file, at + 20, segment.Size);
            Put32(file, at + 24, 4u | (segment.IsWritable ? 2u : 0u) | (segment.IsExecutable ? 1u : 0u));
            Put32(file, at + 28, PageSize);
            segment.Data.Span.CopyTo(file.AsSpan((int)offsets[i]));
        }

        symbolTable.CopyTo(file.AsSpan((int)symbolTableOffset));
        symbolNames.CopyTo(file.AsSpan((int)symbolNamesOffset));
        sectionNames.CopyTo(file.AsSpan((int)sectionNamesOffset));

        // ---- Section headers ----
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var at = (int)sectionHeadersOffset + (SectionHeaderSize * i);
            Put32(file, at, header.Name);
            Put32(file, at + 4, header.Type);
            Put32(file, at + 8, header.Flags);
            Put32(file, at + 12, header.Address);
            Put32(file, at + 16, header.Offset);
            Put32(file, at + 20, header.Size);
            Put32(file, at + 24, header.Link);
            Put32(file, at + 28, header.Info);
            Put32(file, at + 32, header.Alignment);
            Put32(file, at + 36, header.EntrySize);
        }

        return file;
    }

    private static string? Check(ReadOnlySpan<byte> file)
    {
        if (!LooksLikeElf(file))
        {
            return "not an ELF file";
        }

        if (file.Length < HeaderSize)
        {
            return "the file ends inside the ELF header";
        }

        if (file[4] != 1)
        {
            return "a 64-bit ELF file; this core is RV32";
        }

        if (file[5] != 1)
        {
            return "a big-endian ELF file; RISC-V is little-endian";
        }

        if (U16(file, 18) != MachineRiscV)
        {
            return $"an ELF file for another machine (type {U16(file, 18)}); RISC-V is {MachineRiscV}";
        }

        return U16(file, 16) == TypeExecutable ? null : "not an executable: an object file must be linked first";
    }

    private static bool Fits(ReadOnlySpan<byte> file, uint offset, ulong size) => offset + size <= (ulong)file.Length;

    private static ushort U16(ReadOnlySpan<byte> file, int at) => BinaryPrimitives.ReadUInt16LittleEndian(file[at..]);

    private static uint U32(ReadOnlySpan<byte> file, int at) => BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);

    private static void Put16(byte[] file, int at, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), value);

    private static void Put32(byte[] file, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static string SectionName(ReadOnlySpan<byte> file, List<SectionHeader> sections, SectionHeader names, int index) =>
        index >= 0 && index < sections.Count ? String(file, names, sections[index].Name) : string.Empty;

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>The NUL-terminated string at an offset into a string table section.</summary>
    private static string String(ReadOnlySpan<byte> file, SectionHeader table, uint offset)
    {
        if (table.Type != SectionStringTable || offset >= table.Size || !Fits(file, table.Offset, table.Size))
        {
            return string.Empty;
        }

        var bytes = file.Slice((int)(table.Offset + offset), (int)(table.Size - offset));
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private readonly record struct SectionHeader(
        uint Name, uint Type, uint Address, uint Offset, uint Size, uint Link, uint EntrySize,
        uint Flags = 0, uint Alignment = 0, uint Info = 0);

    /// <summary>Names stored end to end, each closed by a zero byte; the first byte is the empty name.</summary>
    private sealed class StringTable
    {
        private readonly List<byte> _bytes = [0];

        public int Length => _bytes.Count;

        public uint Add(string name)
        {
            var offset = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.UTF8.GetBytes(name));
            _bytes.Add(0);
            return offset;
        }

        public void CopyTo(Span<byte> target) => _bytes.ToArray().CopyTo(target);
    }
}
