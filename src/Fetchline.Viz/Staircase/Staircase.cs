using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.Staircase;

/// <summary>One cell of the diagram: where an instruction was during one cycle.</summary>
public readonly record struct StaircaseCell(Stage Stage, Occupancy State);

/// <summary>One row of the diagram: one dynamic instruction, from the cycle it was fetched.</summary>
/// <param name="Seq">The number it was given at fetch. An instruction fetched again has a new row.</param>
/// <param name="FirstCycle">The cycle of its first cell.</param>
/// <param name="Cells">Its stage in each cycle from <paramref name="FirstCycle"/> on, with no gaps.</param>
public sealed record StaircaseRow(ulong Seq, uint Pc, uint Raw, ulong FirstCycle, IReadOnlyList<StaircaseCell> Cells)
{
    /// <summary>The cycle of its last cell.</summary>
    public ulong LastCycle => FirstCycle + (ulong)Cells.Count - 1;

    /// <summary>It was thrown away before it could complete.</summary>
    public bool WasSquashed => Cells[^1].State == Occupancy.Squashed;

    /// <summary>It reached WB.</summary>
    public bool Completed => Cells[^1].Stage == Stage.WriteBack;

    /// <summary>Its cell in a cycle, if it was in the pipeline then.</summary>
    public StaircaseCell? At(ulong cycle) =>
        cycle >= FirstCycle && cycle <= LastCycle ? Cells[(int)(cycle - FirstCycle)] : null;
}

/// <summary>
/// The pipeline diagram of the textbooks: instructions down, cycles across, and in each cell the
/// stage the instruction was in. It is laid out from cycle records and nothing else.
/// </summary>
public sealed class StaircaseLayout
{
    private StaircaseLayout(IReadOnlyList<StaircaseRow> rows, ulong cycles)
    {
        Rows = rows;
        Cycles = cycles;
    }

    /// <summary>The instructions, in the order they were fetched.</summary>
    public IReadOnlyList<StaircaseRow> Rows { get; }

    /// <summary>How many cycles the records cover.</summary>
    public ulong Cycles { get; }

    public static StaircaseLayout Build(IEnumerable<CycleRecord> records)
    {
        var building = new SortedDictionary<ulong, (uint Pc, uint Raw, ulong First, List<StaircaseCell> Cells)>();
        ulong cycles = 0;

        foreach (var record in records)
        {
            cycles = Math.Max(cycles, record.Cycle);
            foreach (var stage in Enum.GetValues<Stage>())
            {
                var view = record[stage];
                if (!view.HasInstruction)
                {
                    continue;
                }

                if (!building.TryGetValue(view.Seq, out var row))
                {
                    row = (view.Pc, view.Raw, record.Cycle, []);
                    building.Add(view.Seq, row);
                }

                row.Cells.Add(new StaircaseCell(stage, view.State));
            }
        }

        return new StaircaseLayout(
            [.. building.Select(pair => new StaircaseRow(pair.Key, pair.Value.Pc, pair.Value.Raw, pair.Value.First, pair.Value.Cells))],
            cycles);
    }

    /// <summary>The row of an instruction, by the number it was given at fetch.</summary>
    public StaircaseRow? Row(ulong seq)
    {
        // Rows are in fetch order and sequence numbers are handed out in that order, with none
        // skipped among the instructions that were ever in a stage; but a search costs little.
        int low = 0, high = Rows.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (Rows[middle].Seq == seq)
            {
                return Rows[middle];
            }

            if (Rows[middle].Seq < seq)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }
}
