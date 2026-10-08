using System.Globalization;
using Fetchline.Core.Asm;

namespace Fetchline.Core.Machine;

/// <summary>What an access wants to do with the bytes it touches.</summary>
public enum Access : byte
{
    Read,
    Write,
    Execute,
}

/// <summary>
/// Which addresses a program may touch, and how. This is what turns a wild pointer into a
/// sentence instead of a silent wrong answer: storing to the code, or reading address
/// 0x90000000, stops the program and says so.
/// </summary>
public sealed class MemoryMap
{
    /// <summary>Where <c>.data</c> starts unless the program says otherwise.</summary>
    public const uint DataBase = 0x1000_0000;

    /// <summary>How far past the start of the data a program may use memory: its heap.</summary>
    public const uint HeapSize = 0x1000_0000;

    /// <summary>Where the stack pointer starts. The stack grows down from here.</summary>
    public const uint StackTop = 0x7FFF_FFF0;

    /// <summary>The lowest address of the stack: one megabyte of it.</summary>
    public const uint StackBase = 0x7FF0_0000;

    private const uint StackEnd = 0x8000_0000;

    // No two regions overlap, so an address is in one region or in none, and remembering the
    // region of the last access is always safe.
    private readonly Region[] _regions;
    private Region _recent;

    private MemoryMap(Region[] regions)
    {
        _regions = regions;
        _recent = regions.Length > 0 ? regions[0] : new Region(string.Empty, 0, 0, SegmentFlags.None);
    }

    /// <summary>The map for a program that runs with the host's system calls.</summary>
    public static MemoryMap ForHost(Program program)
    {
        // The program's own segments come first, each with its own rights.
        var segments = program.Segments
            .Where(segment => segment.Size > 0)
            .Select(segment => new Region(segment.Name, segment.Address, segment.Size, segment.Flags))
            .ToList();

        // The data area starts at the lowest segment that is not code, or at the usual place.
        var data = program.Segments.Where(segment => !segment.IsExecutable).Select(segment => segment.Address);
        var start = data.DefaultIfEmpty(DataBase).Min() & ~(uint)(Memory.PageSize - 1);
        var size = (uint)Math.Min(HeapSize, StackBase - (ulong)Math.Min(start, StackBase));
        const SegmentFlags readWrite = SegmentFlags.Read | SegmentFlags.Write;

        // The heap and the stack are whatever of their ranges the segments leave over, so that
        // a read-only segment inside the data area stays read-only.
        var regions = new List<Region>(segments);
        regions.AddRange(Around(new Region("data and heap", start, size, readWrite), segments));
        regions.AddRange(Around(new Region("stack", StackBase, StackEnd - StackBase, readWrite), segments));
        return new MemoryMap([.. regions]);
    }

    /// <summary>Whether every byte of an access is allowed.</summary>
    public bool Allows(uint address, int bytes, Access access)
    {
        var needed = Needed(access);
        var recent = _recent;
        var offset = address - recent.Start;
        if (offset < recent.Size && recent.Size - offset >= (uint)bytes)
        {
            return (recent.Flags & needed) != 0;
        }

        // The access is somewhere else, or straddles two regions: both of its ends must be allowed.
        var first = Containing(address);
        if (first is null || (first.Flags & needed) == 0)
        {
            return false;
        }

        _recent = first;
        return bytes == 1 || (Containing(address + (uint)bytes - 1) is { } last && (last.Flags & needed) != 0);
    }

    /// <summary>Why an access is not allowed, as a sentence for the person who wrote the program.</summary>
    public string Explain(uint address, int bytes, Access access)
    {
        // Name the first byte that is in the wrong place, which need not be the first byte.
        var needed = Needed(access);
        var firstIsFine = Containing(address) is { } first && (first.Flags & needed) != 0;
        var bad = firstIsFine ? address + (uint)bytes - 1 : address;
        var where = "0x" + bad.ToString("x8", CultureInfo.InvariantCulture);

        return (access, Containing(bad)) switch
        {
            (Access.Write, { } found) => $"a store to {where}, which is in '{found.Name}' and cannot be written",
            (Access.Execute, { } found) => $"a jump to {where}, which is in '{found.Name}' and is not code",
            (Access.Read, { } found) => $"a load from {where}, which is in '{found.Name}' and cannot be read",
            (Access.Write, null) => $"a store to {where}, which is outside the memory this program has",
            (Access.Execute, null) => $"a jump to {where}, which is outside the program",
            _ => $"a load from {where}, which is outside the memory this program has",
        };
    }

    private Region? Containing(uint address)
    {
        foreach (var region in _regions)
        {
            if (address - region.Start < region.Size)
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>The parts of an open range that no segment occupies.</summary>
    private static List<Region> Around(Region open, List<Region> segments)
    {
        var parts = new List<Region>();
        ulong cursor = open.Start;
        var end = (ulong)open.Start + open.Size;

        foreach (var segment in segments.OrderBy(segment => segment.Start))
        {
            ulong start = segment.Start;
            var stop = start + segment.Size;
            if (stop <= cursor || start >= end)
            {
                continue;
            }

            if (start > cursor)
            {
                parts.Add(open with { Start = (uint)cursor, Size = (uint)(start - cursor) });
            }

            cursor = Math.Max(cursor, stop);
        }

        if (cursor < end)
        {
            parts.Add(open with { Start = (uint)cursor, Size = (uint)(end - cursor) });
        }

        return parts;
    }

    private static SegmentFlags Needed(Access access) => access switch
    {
        Access.Write => SegmentFlags.Write,
        Access.Execute => SegmentFlags.Execute,
        _ => SegmentFlags.Read,
    };

    private sealed record Region(string Name, uint Start, uint Size, SegmentFlags Flags);
}
