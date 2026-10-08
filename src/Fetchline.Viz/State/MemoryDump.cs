using System.Text;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.State;

/// <summary>A stretch of memory worth looking at: a section of the program, or the stack.</summary>
/// <param name="Name">The section's own name without its dot, or <c>stack</c>.</param>
/// <param name="Length">In bytes; a whole number of rows.</param>
public sealed record MemoryRegion(string Name, uint Start, uint Length);

/// <summary>One row of a hex dump.</summary>
/// <param name="Written">For each byte, whether it was stored to in the cycle being shown.</param>
/// <param name="Text">The bytes as characters, with a dot for each that is not a printable one.</param>
public sealed record MemoryRow(uint Address, IReadOnlyList<byte> Bytes, IReadOnlyList<bool> Written, string Text);

/// <summary>Memory after the cycle a session is showing, as the rows of a hex dump.</summary>
public static class MemoryDump
{
    /// <summary>How many bytes a row holds: one word, in the order the bytes lie in memory.</summary>
    public const int BytesPerRow = 4;

    // With nothing pushed yet, this much of the stack is shown: where the first things will go.
    private const int EmptyStackRows = 4;

    // The stack is shown from the stack pointer up, but never more of it than this; and a
    // section that is longer than this is shown from its start only so far.
    private const uint MostStack = 256;
    private const uint MostOfASection = 1024;

    /// <summary>
    /// The regions a program gives a reason to look at: each section it has that is not code,
    /// and the stack from the stack pointer up to where it started.
    /// </summary>
    public static IReadOnlyList<MemoryRegion> Regions(Session session)
    {
        var regions = new List<MemoryRegion>();
        if (session.Program is not { } program)
        {
            return regions;
        }

        foreach (var segment in program.Segments)
        {
            if (!segment.IsExecutable && segment.Size > 0)
            {
                regions.Add(new MemoryRegion(
                    segment.Name.TrimStart('.'), segment.Address, Math.Min(RoundUp(segment.Size), MostOfASection)));
            }
        }

        // The stack grows down from its top. While nothing has been pushed there are still a
        // few rows to show, where the first things pushed will go.
        var pointer = session.Registers[2];
        if (pointer is > MemoryMap.StackBase and <= MemoryMap.StackTop)
        {
            var bottom = Math.Min(pointer & ~(uint)(BytesPerRow - 1), MemoryMap.StackTop - (EmptyStackRows * BytesPerRow));
            bottom = Math.Max(bottom, MemoryMap.StackTop - MostStack);
            regions.Add(new MemoryRegion("stack", bottom, MemoryMap.StackTop - bottom));
        }

        return regions;
    }

    /// <summary>The rows of a region.</summary>
    public static IReadOnlyList<MemoryRow> Rows(Session session, MemoryRegion region) =>
        Rows(session, region.Start, (int)(region.Length / BytesPerRow));

    /// <summary>So many rows of memory from an address on.</summary>
    public static IReadOnlyList<MemoryRow> Rows(Session session, uint start, int count)
    {
        // A cycle has one instruction in MEM, so it stores at most once.
        var store = session.Record?.Events.OfType<MemWriteEvent>().FirstOrDefault();
        var rows = new List<MemoryRow>(count);
        var text = new StringBuilder(BytesPerRow);

        for (var row = 0; row < count; row++)
        {
            var address = start + (uint)(row * BytesPerRow);
            var bytes = new byte[BytesPerRow];
            var written = new bool[BytesPerRow];
            text.Clear();
            for (var i = 0; i < BytesPerRow; i++)
            {
                var at = address + (uint)i;
                bytes[i] = session.ReadByte(at);
                written[i] = store is not null && at - store.Address < store.Bytes;
                text.Append(bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.');
            }

            rows.Add(new MemoryRow(address, bytes, written, text.ToString()));
        }

        return rows;
    }

    private static uint RoundUp(uint size) => (size + BytesPerRow - 1) / BytesPerRow * BytesPerRow;
}
