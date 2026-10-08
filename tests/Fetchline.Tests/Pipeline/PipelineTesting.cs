using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;

namespace Fetchline.Tests.Pipeline;

/// <summary>A finished run of the pipeline, cycle by cycle.</summary>
internal sealed record PipelineRun(PipelineMachine Machine, IReadOnlyList<CycleRecord> Records, string Output)
{
    public CycleRecord Last => Records[^1];

    /// <summary>How many cycles the run took.</summary>
    public int Cycles => Records.Count;

    /// <summary>Every commit record, in the order the instructions left the pipeline.</summary>
    public IReadOnlyList<Commit> Commits =>
        [.. Records.SelectMany(record => new[] { record.Commit, record.End }).OfType<Commit>()];

    /// <summary>The final value of a register, by either of its names.</summary>
    public uint this[string register]
    {
        get
        {
            Assert.True(Registers.TryParse(register, out var index), $"'{register}' is not a register");
            return Machine.Hart.X[index];
        }
    }

    /// <summary>
    /// The stages one instruction was in, cycle by cycle, as the textbooks draw a row of the
    /// diagram: "IF ID EX MEM WB", with a repeated name where it was held and "xx" where it was
    /// thrown away. The instruction is named by the order it was fetched in, from one.
    /// </summary>
    public string Row(ulong seq)
    {
        var cells = new List<string>();
        foreach (var record in Records)
        {
            foreach (var stage in Enum.GetValues<Stage>())
            {
                var view = record[stage];
                if (view.HasInstruction && view.Seq == seq)
                {
                    cells.Add(Name(stage) + (view.State == Occupancy.Squashed ? " xx" : string.Empty));
                }
            }
        }

        return string.Join(' ', cells);
    }

    /// <summary>The cycle in which an instruction was in a stage; zero if it never was.</summary>
    public ulong CycleOf(ulong seq, Stage stage) =>
        Records.FirstOrDefault(record => record[stage] is { HasInstruction: true } view && view.Seq == seq)?.Cycle ?? 0;

    private static string Name(Stage stage) => stage switch
    {
        Stage.Fetch => "IF",
        Stage.Decode => "ID",
        Stage.Execute => "EX",
        Stage.Memory => "MEM",
        _ => "WB",
    };
}

internal static class PipelineTesting
{
    /// <summary>Assembles a program and runs it on the pipeline until it stops.</summary>
    public static PipelineRun Run(string source, int maxCycles = 100_000)
    {
        var output = new StringWriter();
        var machine = new PipelineMachine(AssemblerTesting.Assemble(source), output);
        var records = new List<CycleRecord>();

        while (!machine.IsFinished && records.Count < maxCycles)
        {
            records.Add(machine.Step());
        }

        return new PipelineRun(machine, records, output.ToString());
    }

    /// <summary>Runs a program that must reach the end of its code.</summary>
    public static PipelineRun RunToEnd(string source)
    {
        var run = Run(source);
        Assert.True(
            run.Last.Stop == StopReason.EndOfProgram,
            $"stopped with {run.Last.Stop} after {run.Cycles} cycles: {run.Commits.LastOrDefault().Message}");
        return run;
    }
}
