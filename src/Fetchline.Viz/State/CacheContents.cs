using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.State;

/// <summary>What was done to a way of a cache in the cycle on screen.</summary>
public enum CacheTouch : byte
{
    None,

    /// <summary>The block asked for was here.</summary>
    Hit,

    /// <summary>The block asked for was not in the set, and was brought in here.</summary>
    Miss,
}

/// <summary>One way of one set, for the cache pane.</summary>
/// <param name="Valid">It holds a block.</param>
/// <param name="Block">The address the block begins at.</param>
/// <param name="Touch">What the cycle on screen did here.</param>
/// <param name="NextOut">
/// The set is full and this is the way that was used longest ago: the block here is the one
/// the next miss in this set puts out.
/// </param>
public readonly record struct CacheCell(bool Valid, uint Block, CacheTouch Touch, bool NextOut);

/// <summary>One set: its number and its ways.</summary>
public sealed record CacheRow(int Set, IReadOnlyList<CacheCell> Ways);

/// <summary>
/// What a cache holds after the cycle on screen, a row for each set. Like the registers and the
/// memory beside it, it is what the records say and nothing else: the session has not looked
/// inside the machine's cache to find out.
/// </summary>
public static class CacheContents
{
    /// <summary>The caches a pipeline has, in the order instruction, data.</summary>
    public static IReadOnlyList<CacheKind> Kinds(PipelineConfig config)
    {
        var kinds = new List<CacheKind>(2);
        if (config.InstructionCache is not null)
        {
            kinds.Add(CacheKind.Instruction);
        }

        if (config.DataCache is not null)
        {
            kinds.Add(CacheKind.Data);
        }

        return kinds;
    }

    /// <summary>The shape of one of a pipeline's caches, or null when it has no such cache.</summary>
    public static CacheConfig? Shape(PipelineConfig config, CacheKind kind) =>
        kind == CacheKind.Instruction ? config.InstructionCache : config.DataCache;

    /// <summary>The rows of a cache the session's pipeline has; none when it has no such cache or no program.</summary>
    public static IReadOnlyList<CacheRow> Of(Session session, CacheKind kind)
    {
        if (Shape(session.Config, kind) is not { } shape || session.Program is null)
        {
            return [];
        }

        // A cycle asks a cache at most once.
        CacheEvent? touched = null;
        foreach (var item in session.Record?.Events ?? [])
        {
            if (item is CacheEvent access && access.Kind == kind)
            {
                touched = access;
            }
        }

        var rows = new List<CacheRow>(shape.Sets);
        for (var set = 0; set < shape.Sets; set++)
        {
            var lines = new (bool Valid, uint Block, ulong Used)[shape.Ways];
            var (oldest, full) = (0, true);
            for (var way = 0; way < shape.Ways; way++)
            {
                lines[way] = session.CacheLine(kind, set, way);
                full &= lines[way].Valid;
                if (lines[way].Used < lines[oldest].Used)
                {
                    oldest = way;
                }
            }

            var cells = new CacheCell[shape.Ways];
            for (var way = 0; way < shape.Ways; way++)
            {
                var touch = touched is { } access && access.Set == set && access.Way == way
                    ? access.Hit ? CacheTouch.Hit : CacheTouch.Miss
                    : CacheTouch.None;

                // With one way there is nothing to choose between, so nothing to point out.
                cells[way] = new CacheCell(lines[way].Valid, lines[way].Block, touch, full && shape.Ways > 1 && way == oldest);
            }

            rows.Add(new CacheRow(set, cells));
        }

        return rows;
    }
}
