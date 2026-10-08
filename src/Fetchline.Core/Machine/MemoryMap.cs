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

    // Exact regions are the program's own segments, each with its own rights. Open regions are
    // the heap and the stack: readable and writable, and searched only when no segment matches,
    // so that a read-only segment inside the data area stays read-only.
    private readonly List<Region> _segments = [];
    private readonly List<Region> _open = [];

    /// <summary>The map for a program that runs with the host's system calls.</summary>
    public static MemoryMap ForHost(Program program)
    {
        var map = new MemoryMap();
        foreach (var segment in program.Segments)
        {
            map._segments.Add(new Region(segment.Name, segment.Address, segment.Size, segment.Flags));
        }

        // The data area starts at the lowest segment that is not code, or at the usual place.
        var data = program.Segments.Where(segment => !segment.IsExecutable).Select(segment => segment.Address);
        var start = data.DefaultIfEmpty(DataBase).Min() & ~(uint)(Memory.PageSize - 1);
        var size = (uint)Math.Min(HeapSize, StackBase - (ulong)Math.Min(start, StackBase));
        if (size > 0)
        {
            map._open.Add(new Region("data and heap", start, size, SegmentFlags.Read | SegmentFlags.Write));
        }

        map._open.Add(new Region("stack", StackBase, StackEnd - StackBase, SegmentFlags.Read | SegmentFlags.Write));
        return map;
    }

    /// <summary>Whether every byte of an access is allowed.</summary>
    public bool Allows(uint address, int bytes, Access access) =>
        Find(address, access) is not null && (bytes == 1 || Find(address + (uint)bytes - 1, access) is not null);

    /// <summary>Why an access is not allowed, as a sentence for the person who wrote the program.</summary>
    public string Explain(uint address, int bytes, Access access)
    {
        // Name the first byte that is in the wrong place, which need not be the first byte.
        var bad = Find(address, access) is null ? address : address + (uint)bytes - 1;
        var where = "0x" + bad.ToString("x8", CultureInfo.InvariantCulture);
        var region = Containing(bad);

        return (access, region) switch
        {
            (Access.Write, { } found) => $"a store to {where}, which is in '{found.Name}' and cannot be written",
            (Access.Execute, { } found) => $"a jump to {where}, which is in '{found.Name}' and is not code",
            (Access.Read, { } found) => $"a load from {where}, which is in '{found.Name}' and cannot be read",
            (Access.Write, null) => $"a store to {where}, which is outside the memory this program has",
            (Access.Execute, null) => $"a jump to {where}, which is outside the program",
            _ => $"a load from {where}, which is outside the memory this program has",
        };
    }

    private Region? Find(uint address, Access access)
    {
        var region = Containing(address);
        return region is { } found && (found.Flags & Needed(access)) != 0 ? found : null;
    }

    private Region? Containing(uint address)
    {
        foreach (var region in _segments)
        {
            if (address - region.Start < region.Size)
            {
                return region;
            }
        }

        foreach (var region in _open)
        {
            if (address - region.Start < region.Size)
            {
                return region;
            }
        }

        return null;
    }

    private static SegmentFlags Needed(Access access) => access switch
    {
        Access.Write => SegmentFlags.Write,
        Access.Execute => SegmentFlags.Execute,
        _ => SegmentFlags.Read,
    };

    private readonly record struct Region(string Name, uint Start, uint Size, SegmentFlags Flags);
}
