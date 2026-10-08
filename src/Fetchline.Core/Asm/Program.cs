using System.Buffers.Binary;

namespace Fetchline.Core.Asm;

[Flags]
public enum SegmentFlags : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}

/// <summary>A contiguous piece of a program's memory image.</summary>
/// <param name="Data">The initialised bytes. May be shorter than <paramref name="Size"/>.</param>
/// <param name="Size">How much memory the segment occupies; what lies past the data reads as zero.</param>
public sealed record Segment(string Name, uint Address, ReadOnlyMemory<byte> Data, uint Size, SegmentFlags Flags)
{
    /// <summary>The first address after the segment.</summary>
    public uint End => Address + Size;

    public bool IsExecutable => (Flags & SegmentFlags.Execute) != 0;

    public bool IsWritable => (Flags & SegmentFlags.Write) != 0;

    public bool Contains(uint address) => address - Address < Size;
}

public enum SymbolKind : byte
{
    /// <summary>An address: a label.</summary>
    Label,

    /// <summary>A number given a name with <c>.equ</c>, <c>.set</c> or <c>=</c>.</summary>
    Constant,
}

/// <param name="Section">The section a label is in (<c>.text</c>, <c>.data</c>…); null for a constant.</param>
public sealed record Symbol(string Name, uint Value, SymbolKind Kind, bool IsGlobal, string? Section);

/// <summary>Where one machine instruction came from in the source.</summary>
/// <param name="Start">The offset of the statement from the start of the source.</param>
/// <param name="Part">
/// Which instruction of the statement this is, from zero. A pseudo-instruction such as
/// <c>la</c> becomes two machine instructions, and both map back to the one line.
/// </param>
/// <param name="PartCount">How many machine instructions the statement became.</param>
public readonly record struct SourceMapEntry(
    uint Address, int Line, int Column, int Start, int Length, int Part, int PartCount);

/// <summary>The link between machine instructions and source lines, in both directions.</summary>
public sealed class SourceMap
{
    private readonly SourceMapEntry[] _entries;
    private readonly ILookup<int, SourceMapEntry> _byLine;

    public SourceMap(IEnumerable<SourceMapEntry> entries)
    {
        _entries = [.. entries.OrderBy(entry => entry.Address)];
        _byLine = _entries.ToLookup(entry => entry.Line);
    }

    public static SourceMap Empty { get; } = new([]);

    /// <summary>Every instruction, by ascending address.</summary>
    public IReadOnlyList<SourceMapEntry> Entries => _entries;

    /// <summary>The source of the instruction at an address, if an instruction was assembled there.</summary>
    public bool TryGetByAddress(uint address, out SourceMapEntry entry)
    {
        int low = 0, high = _entries.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (_entries[middle].Address == address)
            {
                entry = _entries[middle];
                return true;
            }

            if (_entries[middle].Address < address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>The instructions a source line became, by ascending address.</summary>
    public IEnumerable<SourceMapEntry> ByLine(int line) => _byLine[line];
}

/// <summary>
/// A program ready to run: its memory image, its symbols and where it starts. The assembler
/// makes one from source and the ELF reader makes one from a file; the machines only see this.
/// </summary>
public sealed class Program
{
    private readonly Dictionary<string, Symbol> _byName;
    private readonly Dictionary<uint, string> _labelAt;

    public Program(
        IEnumerable<Segment> segments, IEnumerable<Symbol> symbols, uint entry,
        SourceMap? sourceMap = null, string? source = null)
    {
        Segments = [.. segments.OrderBy(segment => segment.Address)];
        Symbols = [.. symbols.OrderBy(symbol => symbol.Value).ThenBy(symbol => symbol.Name, StringComparer.Ordinal)];
        Entry = entry;
        SourceMap = sourceMap ?? SourceMap.Empty;
        Source = source;

        _byName = [];
        _labelAt = [];
        foreach (var symbol in Symbols)
        {
            _byName.TryAdd(symbol.Name, symbol);

            // When several labels share an address, a listing shows one of them. Names the
            // programmer chose beat compiler-local ones (".L3"), and global beats local.
            if (symbol.Kind == SymbolKind.Label
                && (!_labelAt.TryGetValue(symbol.Value, out var current) || Rank(symbol) < Rank(_byName[current])))
            {
                _labelAt[symbol.Value] = symbol.Name;
            }
        }
    }

    public IReadOnlyList<Segment> Segments { get; }

    public IReadOnlyList<Symbol> Symbols { get; }

    /// <summary>The address of the first instruction to run.</summary>
    public uint Entry { get; }

    public SourceMap SourceMap { get; }

    /// <summary>The source this was assembled from; null for a program read from an ELF file.</summary>
    public string? Source { get; }

    /// <summary>The segment that holds the code.</summary>
    public Segment? Text => Segments.FirstOrDefault(segment => segment.IsExecutable);

    public bool TryGetSymbol(string name, out Symbol symbol) => _byName.TryGetValue(name, out symbol!);

    /// <summary>The label at an address, when there is one.</summary>
    public string? LabelAt(uint address) => _labelAt.GetValueOrDefault(address);

    /// <summary>The segment an address falls in.</summary>
    public Segment? SegmentAt(uint address) => Segments.FirstOrDefault(segment => segment.Contains(address));

    /// <summary>The little-endian word at an address inside a segment; zero past its initialised data.</summary>
    public bool TryReadWord(uint address, out uint word)
    {
        word = 0;
        var segment = SegmentAt(address);
        if (segment is null || !segment.Contains(address + 3))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        var offset = (int)(address - segment.Address);
        var available = Math.Clamp(segment.Data.Length - offset, 0, 4);
        segment.Data.Span.Slice(Math.Min(offset, segment.Data.Length), available).CopyTo(bytes);
        word = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static int Rank(Symbol symbol) =>
        (symbol.IsGlobal ? 0 : 2) + (symbol.Name.StartsWith('.') || symbol.Name.StartsWith('$') ? 1 : 0);
}
