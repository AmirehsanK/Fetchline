using System.Text.Json;
using System.Xml.Linq;
using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Cli;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using Fetchline.Viz.Export;
using Fetchline.Viz.Staircase;

namespace Fetchline.Tests.Viz;

/// <summary>A run written out for other programs: as JSON, as a Kanata log, and as a picture.</summary>
public class ExportTests
{
    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    private static (Program Program, List<CycleRecord> Records) Run(string source, PipelineConfig? config = null)
    {
        var program = AssemblerTesting.Assemble(source);
        var machine = new PipelineMachine(program, config: config);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished && machine.Stopped != Core.Trace.StopReason.Breakpoint && records.Count < 50_000)
        {
            records.Add(machine.Step());
        }

        return (program, records);
    }

    [Fact]
    public void TheJsonIsTheRecordStreamWrittenOut()
    {
        var (program, records) = Run(Example("load-use.s"));
        using var json = JsonDocument.Parse(JsonTrace.Write(program, PipelineConfig.Default, records));
        var root = json.RootElement;

        Assert.Equal(("fetchline-trace", 1), (root.GetProperty("format").GetString(), root.GetProperty("version").GetInt32()));
        Assert.Equal("forwarding", root.GetProperty("config").GetProperty("hazards").GetString());
        Assert.Equal((8, 3, 1, 2), (
            root.GetProperty("summary").GetProperty("cycles").GetInt32(),
            root.GetProperty("summary").GetProperty("instructions").GetInt32(),
            root.GetProperty("summary").GetProperty("stalls").GetInt32(),
            root.GetProperty("summary").GetProperty("forwards").GetInt32()));

        var cycles = root.GetProperty("cycles");
        Assert.Equal(records.Count, cycles.GetArrayLength());

        // Cycle 3 is the stall: the add is held in ID behind the load in EX, and says why.
        var third = cycles[2];
        Assert.Equal(3, third.GetProperty("cycle").GetInt32());
        var decode = third.GetProperty("stages").GetProperty("ID");
        Assert.Equal(("add x5, x4, x6", "held", "00000004"), (
            decode.GetProperty("text").GetString(), decode.GetProperty("state").GetString(), decode.GetProperty("pc").GetString()));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("stages").GetProperty("WB").ValueKind);
        var stall = Assert.Single(third.GetProperty("events").EnumerateArray());
        Assert.Equal(("stall", "load-use", "x4", 1), (
            stall.GetProperty("type").GetString(), stall.GetProperty("cause").GetString(),
            stall.GetProperty("register").GetString(), stall.GetProperty("producer").GetInt32()));
        Assert.Equal("held", third.GetProperty("wires").GetProperty("nextPcFrom").GetString());
        Assert.Equal("pc+4", cycles[0].GetProperty("wires").GetProperty("nextPcFrom").GetString());

        // Cycle 5: the load completes and its value is forwarded to the add.
        var fifth = cycles[4];
        Assert.Equal(("x4", "00000000"), (
            fifth.GetProperty("commit").GetProperty("register").GetString(), fifth.GetProperty("commit").GetProperty("pc").GetString()));
        Assert.Contains(fifth.GetProperty("events").EnumerateArray(), item =>
            item.GetProperty("type").GetString() == "forward" && item.GetProperty("from").GetString() == "MEM/WB"
            && item.GetProperty("operand").GetString() == "rs1");

        // The run ends without an instruction, and the last cycle says how.
        Assert.Equal("end-of-program", cycles[cycles.GetArrayLength() - 1].GetProperty("end").GetProperty("stop").GetString());
    }

    [Fact]
    public void EveryKindOfEventAndEveryWordIsWrittenTheSameWay()
    {
        // Stores, branches, a trap and a system instruction, on a pipeline that stalls and predicts.
        var source = Example("bubble-sort.s") + "\nlw t0, 1(zero)\n";
        var config = new PipelineConfig { Hazards = HazardHandling.StallOnly, Predictor = Predictor.TwoBit, MulDivCycles = 3 };
        var (program, records) = Run(source, config);
        using var json = JsonDocument.Parse(JsonTrace.Write(program, config, records));

        var kinds = new HashSet<string>();
        foreach (var cycle in json.RootElement.GetProperty("cycles").EnumerateArray())
        {
            foreach (var item in cycle.GetProperty("events").EnumerateArray())
            {
                kinds.Add(item.GetProperty("type").GetString()!);
            }

            // A word is eight hex digits wherever it appears.
            foreach (var wire in cycle.GetProperty("wires").EnumerateObject().Where(wire => wire.Name != "nextPcFrom"))
            {
                Assert.Matches("^[0-9a-f]{8}$", wire.Value.GetString());
            }
        }

        Assert.Superset(new HashSet<string> { "stall", "flush", "branch", "reg-write", "mem-read", "mem-write", "commit" }, kinds);
        Assert.Equal(records.Sum(record => record.Events.Count),
            json.RootElement.GetProperty("cycles").EnumerateArray().Sum(cycle => cycle.GetProperty("events").GetArrayLength()));
    }

    [Fact]
    public void TheKanataLogOfTheLoadUseHazardIsTheOneKeptHere()
    {
        var (program, records) = Run(Example("load-use.s"));

        Golden.Check("load-use.kanata.log", KanataTrace.Write(program, records));
    }

    [Theory]
    [InlineData("load-use.s")]
    [InlineData("bubble-sort.s")]
    [InlineData("factorial.s")]
    [InlineData("fib.s")]
    public void AKanataLogKeepsToTheRulesOfTheFormat(string example)
    {
        foreach (var config in new[]
        {
            PipelineConfig.Default,
            new PipelineConfig { Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, MulDivCycles = 3 },
            new PipelineConfig { Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit },
        })
        {
            var (program, records) = Run(Example(example), config);
            var stats = PipelineStats.Of(records);
            var lines = KanataTrace.Write(program, records).TrimEnd('\n').Split('\n');
            var where = $"{example} with {Configurations.Name(config)}";

            Assert.Equal("Kanata\t0004", lines[0]);
            Assert.Equal("C=\t1", lines[1]);

            // Per instruction: the stage it is in, in each lane, and whether it has gone.
            var alive = new Dictionary<int, Dictionary<int, string>>();
            var gone = new HashSet<int>();
            var (elapsed, completed, flushed, introduced) = (1, 0, 0, 0);
            foreach (var line in lines.Skip(2))
            {
                var fields = line.Split('\t');
                if (fields[0] == "C")
                {
                    Assert.Equal("1", fields[1]);
                    elapsed++;
                    continue;
                }

                Assert.True(fields.Length == 4, $"{where}: '{line}'");
                var id = int.Parse(fields[1]);
                switch (fields[0])
                {
                    case "I":
                        // Ids count up from zero in the order the instructions appear.
                        Assert.True(id == introduced++, $"{where}: '{line}'");
                        alive.Add(id, []);
                        break;
                    case "L":
                        Assert.True(alive.ContainsKey(id) && fields[2] == "0" && fields[3].Length > 10, $"{where}: '{line}'");
                        break;
                    case "S":
                        Assert.True(alive.ContainsKey(id) && !gone.Contains(id), $"{where}: '{line}'");
                        Assert.True(alive[id].TryAdd(int.Parse(fields[2]), fields[3]), $"{where}: '{line}' begins a stage inside another");
                        break;
                    case "E":
                        Assert.True(alive[id].Remove(int.Parse(fields[2]), out var stage) && stage == fields[3], $"{where}: '{line}'");
                        break;
                    case "R":
                        Assert.True(alive[id].Count == 0 && gone.Add(id), $"{where}: '{line}'");
                        if (fields[3] == "0")
                        {
                            Assert.True(int.Parse(fields[2]) == completed++, $"{where}: '{line}'");
                        }
                        else
                        {
                            Assert.Equal("1", fields[3]);
                            flushed++;
                        }

                        break;
                    case "W":
                        // An arrow needs a consumer that is still there and a producer that was introduced.
                        Assert.True(alive.ContainsKey(id) && !gone.Contains(id) && alive.ContainsKey(int.Parse(fields[2])), $"{where}: '{line}'");
                        break;
                    default:
                        Assert.Fail($"{where}: '{line}' is not a command");
                        break;
                }
            }

            // Everything that was fetched has gone one way or the other, and the counts are the run's.
            // An instruction still in the pipeline when a run stops at an ebreak has not gone yet.
            Assert.True((ulong)completed == stats.Instructions && flushed == stats.Squashed, where);
            Assert.True(elapsed >= records.Count && elapsed <= records.Count + 1, where);
            Assert.Equal(stats.Forwards > 0, lines.Any(line => line.StartsWith("W\t", StringComparison.Ordinal)));
            Assert.Equal(stats.Stalls > 0, lines.Any(line => line.EndsWith("\t1\tstl", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void ThePictureIsTheGridWithItsMarksAndItsArrows()
    {
        var (program, records) = Run("lw t0, 0(sp)\naddi t1, t0, 1\nbeqz zero, on\nnop\nnop\non: add t2, t1, t1\n");
        var grid = StaircaseGrid.Build(records, new InstructionLabels(program), 1, records[^1].Cycle);
        var text = StaircaseSvg.Write(grid, "a <test> & its picture");

        // It is well-formed, stands on its own, and says what it is.
        var svg = XDocument.Parse(text).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(ns + "svg", svg.Name);
        Assert.Equal("a <test> & its picture", svg.Element(ns + "title")!.Value);
        Assert.DoesNotContain("href", text);
        Assert.DoesNotContain("<style", text);
        Assert.DoesNotContain("url(", text);

        // One text for each cycle number, each label and each cell that holds something.
        var cells = grid.Rows.Sum(row => row.Cells.Count(cell => cell.Kind != CellKind.Empty));
        Assert.Equal(grid.Columns + grid.Rows.Count + cells, svg.Elements(ns + "text").Count());
        Assert.Contains(svg.Elements(ns + "text"), element => element.Value.Contains("beqz", StringComparison.Ordinal));

        // A held stage is a lit box behind unlit letters, a squashed one is struck out, and
        // each forward is one line.
        var held = grid.Rows.Sum(row => row.Cells.Count(cell => cell.Kind == CellKind.Held));
        Assert.True(held > 0 && grid.Arrows.Count > 0);
        Assert.Equal(1 + held, svg.Elements(ns + "rect").Count());
        Assert.Equal(
            grid.Rows.Sum(row => row.Cells.Count(cell => cell.Kind == CellKind.Squashed)),
            svg.Elements(ns + "text").Count(element => element.Attribute("text-decoration") is not null));
        Assert.Equal(grid.Arrows.Count, svg.Elements(ns + "path").Count());

        // Everything drawn is inside the picture.
        var width = double.Parse(svg.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(svg.Elements(ns + "text"), element =>
            Assert.InRange(double.Parse(element.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture), 0, width - 20));
    }

    [Fact]
    public void ThePlaygroundSavesTheRunAsFarAsTheCycleOnScreen()
    {
        var messages = Fetchline.Viz.Explain.EnglishMessages.Instance;
        var session = new Session(Example("bubble-sort.s"));

        // Nothing has run, so there is nothing to save; nor is there when the source has errors.
        Assert.All(Enum.GetValues<ExportKind>(), kind => Assert.Null(SessionExport.Make(session, kind, messages)));
        Assert.Null(SessionExport.Make(new Session("bogus"), ExportKind.Json, messages));

        session.Seek(300);
        session.Seek(200);
        var json = SessionExport.Make(session, ExportKind.Json, messages)!;
        using var parsed = JsonDocument.Parse(json.Text);
        Assert.Equal(("fetchline-trace.json", "application/json"), (json.Name, json.MediaType));
        Assert.Equal(200, parsed.RootElement.GetProperty("cycles").GetArrayLength());

        var kanata = SessionExport.Make(session, ExportKind.Kanata, messages)!;
        Assert.Equal("fetchline-trace.kanata.log", kanata.Name);
        Assert.StartsWith("Kanata\t0004\nC=\t1\n", kanata.Text);
        Assert.InRange(kanata.Text.Split('\n').Count(line => line == "C\t1"), 199, 200);

        // A picture is of the last cycles only, and the PNG is the same picture for the page to draw.
        var svg = SessionExport.Make(session, ExportKind.Svg, messages)!;
        var png = SessionExport.Make(session, ExportKind.Png, messages)!;
        Assert.Equal(("fetchline-diagram.svg", "image/svg+xml", "fetchline-diagram.png", "image/png"), (svg.Name, svg.MediaType, png.Name, png.MediaType));
        Assert.Equal(svg.Text, png.Text);
        Assert.Contains(">200</text>", svg.Text);
        Assert.Contains(">81</text>", svg.Text);
        Assert.DoesNotContain(">80</text>", svg.Text);
    }

    [Fact]
    public void TheCommandLineWritesEachOfThemToTheTerminalOrToAFile()
    {
        var source = Repo.PathOf("examples", "load-use.s");

        Assert.StartsWith("Kanata\t0004\nC=\t1\n", CommandLine.RunOk("trace", source, "--format", "kanata"));
        Assert.StartsWith("{\"format\":\"fetchline-trace\"", CommandLine.RunOk("trace", source, "--format", "json"));
        Assert.Contains("\"hazards\":\"stall\"", CommandLine.RunOk("trace", source, "--format", "json", "--hazards", "stall"));

        // The picture is of the cycles asked for, like the text.
        var picture = CommandLine.RunOk("trace", source, "--format", "svg", "--cycles", "4");
        Assert.StartsWith("<svg ", picture);
        Assert.Contains(">4</text>", picture);
        Assert.DoesNotContain(">5</text>", picture);

        var file = Path.Combine(Path.GetTempPath(), $"fetchline-{Guid.NewGuid():N}.log");
        try
        {
            Assert.Equal(string.Empty, CommandLine.RunOk("trace", source, "--format", "kanata", "--output", file));
            Assert.Equal(CommandLine.RunOk("trace", source, "--format", "kanata"), File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }

        var (exitCode, _, error) = CommandLine.Run("trace", source, "--format", "png");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("png", error);
    }
}
