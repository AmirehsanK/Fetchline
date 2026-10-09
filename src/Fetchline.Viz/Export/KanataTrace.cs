using System.Globalization;
using System.Text;
using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.Export;

/// <summary>
/// A run as a Kanata log, the format the pipeline viewer Konata reads. A line is a command and
/// its arguments, separated by tabs:
/// <code>
/// Kanata  0004                the header: the name and the version of the format
/// C=      cycle               the cycle the log begins at
/// C       n                   n cycles go by
/// I       id  seq  thread     an instruction appears; ids count up from zero in file order
/// L       id  type  text      its label: type 0 beside its row, type 1 when pointed at
/// S       id  lane  stage     it enters a stage
/// E       id  lane  stage     it leaves a stage
/// R       id  retire  type    it is gone: type 0 completed, type 1 thrown away
/// W       id  producer  type  it took a value from another instruction: an arrow
/// </code>
/// An instruction that is held stays in its stage, so its box is as long as the wait, and the
/// wait itself is a stage named <c>stl</c> in lane 1, which is where the format's own examples put
/// a stall. One that is squashed ends in the cycle after the one it was thrown away in, as in
/// the diagram. Konata draws an arrow only between stages with an X in their names, which EX has.
/// </summary>
public static class KanataTrace
{
    public const string Header = "Kanata\t0004";

    /// <summary>The name of the stage, in lane 1, that an instruction is in while it is held.</summary>
    public const string StallStage = "stl";

    public static string Write(Program program, IReadOnlyList<CycleRecord> records)
    {
        var labels = new InstructionLabels(program);
        var text = new StringBuilder();
        text.Append(Header).Append('\n');

        // The id each instruction was given, and the stage it is in as far as the log has said.
        var ids = new Dictionary<ulong, int>();
        var inside = new Dictionary<ulong, Stage>();
        var waiting = new HashSet<ulong>();

        // What left the pipeline in the cycle before: said at the start of the next, when its
        // last stage is over.
        var leaving = new List<(ulong Seq, bool Completed)>();
        var retired = 0;

        void Line(string command, int id, int second, string third) =>
            text.Append(command).Append('\t').Append(Number(id)).Append('\t').Append(Number(second)).Append('\t')
                .Append(third).Append('\n');

        void Leave()
        {
            foreach (var (seq, completed) in leaving)
            {
                if (waiting.Remove(seq))
                {
                    Line("E", ids[seq], 1, StallStage);
                }

                // Only what completes is counted; what is thrown away takes the number it would
                // have had, as the format allows.
                Line("E", ids[seq], 0, AsciiTrace.Name(inside[seq]));
                Line("R", ids[seq], completed ? retired++ : retired, completed ? "0" : "1");
                inside.Remove(seq);
            }

            leaving.Clear();
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            text.Append(i == 0 ? "C=\t" + Number(record.Cycle) : "C\t1").Append('\n');
            Leave();

            // Oldest first, so that an instruction is always introduced before anything points at it.
            foreach (var stage in (ReadOnlySpan<Stage>)[Stage.WriteBack, Stage.Memory, Stage.Execute, Stage.Decode, Stage.Fetch])
            {
                var view = record[stage];
                if (!view.HasInstruction)
                {
                    continue;
                }

                if (!ids.TryGetValue(view.Seq, out var id))
                {
                    id = ids.Count;
                    ids.Add(view.Seq, id);
                    var line = labels.Describe(view.Pc, view.Raw);
                    var label = line.Operands.Length == 0 ? line.Mnemonic : line.Mnemonic + " " + line.Operands;
                    text.Append("I\t").Append(Number(id)).Append('\t').Append(view.Seq.ToString(CultureInfo.InvariantCulture))
                        .Append("\t0\n");
                    Line("L", id, 0, view.Pc.ToString("x8", CultureInfo.InvariantCulture) + ": " + Clean(label));
                }

                if (!inside.TryGetValue(view.Seq, out var was) || was != stage)
                {
                    if (inside.ContainsKey(view.Seq))
                    {
                        Line("E", id, 0, AsciiTrace.Name(was));
                    }

                    Line("S", id, 0, AsciiTrace.Name(stage));
                    inside[view.Seq] = stage;
                }

                if (view.State == Occupancy.Held && waiting.Add(view.Seq))
                {
                    Line("S", id, 1, StallStage);
                }
                else if (view.State != Occupancy.Held && waiting.Remove(view.Seq))
                {
                    Line("E", id, 1, StallStage);
                }

                if (view.State == Occupancy.Squashed)
                {
                    leaving.Add((view.Seq, false));
                }
                else if (stage == Stage.WriteBack)
                {
                    leaving.Add((view.Seq, true));
                }
            }

            foreach (var forward in record.Events.OfType<ForwardEvent>())
            {
                if (ids.TryGetValue(forward.Seq, out var consumer) && ids.TryGetValue(forward.Producer, out var producer))
                {
                    Line("W", consumer, producer, "0");
                }
            }
        }

        if (leaving.Count > 0)
        {
            text.Append("C\t1\n");
            Leave();
        }

        return text.ToString();
    }

    // A label is one field of one line.
    private static string Clean(string label) => label.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
