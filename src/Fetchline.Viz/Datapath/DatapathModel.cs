using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.Datapath;

/// <summary>What a part of the datapath is, which decides the shape it is drawn with.</summary>
public enum PartKind : byte
{
    /// <summary>The program counter.</summary>
    Register,

    /// <summary>One of the four latches between the stages.</summary>
    Latch,

    /// <summary>Instruction or data memory, or the register file.</summary>
    Store,

    Adder,

    Mux,

    Alu,

    /// <summary>Logic that computes a value: the immediate generator.</summary>
    Logic,

    /// <summary>Logic that decides something and steers the rest: the hazard unit, the forwarding unit.</summary>
    Unit,
}

/// <summary>What a wire carries.</summary>
public enum WireKind : byte
{
    /// <summary>A value: an address, an operand, a result.</summary>
    Data,

    /// <summary>A decision: hold this, select that.</summary>
    Control,
}

/// <summary>What is on a wire. A language has a name for each, as a datapath is labelled.</summary>
public enum Signal : byte
{
    Pc,
    PcPlus4,
    NextPc,
    Predicted,
    Target,
    Instruction,
    Rs1,
    Rs2,
    Rs1Value,
    Rs2Value,
    Imm,
    AluA,
    AluB,
    Result,
    Forwarded,
    Address,
    WriteData,
    ReadData,
    RdValue,
    Hold,
    Select,
}

/// <summary>A point of the drawing, in its own units, with y downwards.</summary>
public readonly record struct Point(int X, int Y);

/// <summary>One part of the datapath: a box at a place.</summary>
/// <param name="Stage">The stage it belongs to; a latch belongs to the stage it closes.</param>
/// <param name="Label">What is written on it. Empty for a multiplexer, which is known by its shape.</param>
public sealed record Part(string Id, PartKind Kind, Stage Stage, int X, int Y, int Width, int Height, string Label)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>The point is on the outline of the part.</summary>
    public bool HasOnOutline(Point point) =>
        point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom
        && (point.X == X || point.X == Right || point.Y == Y || point.Y == Bottom);
}

/// <summary>One wire: from the outline of one part to the outline of another, in straight runs.</summary>
/// <param name="Signal">What it carries.</param>
/// <param name="Points">Its corners, the two ends included. Every run is level or upright.</param>
public sealed record Wire(string Id, string From, string To, Signal Signal, WireKind Kind, IReadOnlyList<Point> Points);

/// <summary>
/// The datapath of the pipeline as data: its parts, where they are, and the wires between them.
/// Nothing here is a drawing. The page turns it into one, a test holds it to the rules of one
/// (wires begin and end on the parts they name, run level or upright, and go round what is in
/// their way), and <see cref="DatapathView"/> says which of it is in use in a cycle.
///
/// The datapath is that of the pipeline as it is built: with the switches set to stall instead
/// of forwarding there is no forwarding unit to draw, and a branch decided in ID has a
/// comparator there in place of the branch logic in EX.
/// </summary>
public sealed class DatapathModel
{
    /// <summary>The size of the drawing, in the units the parts are placed in.</summary>
    public const int Width = 720;

    public const int Height = 400;

    private readonly Dictionary<string, Part> _parts = [];
    private readonly List<Wire> _wires = [];

    private DatapathModel(PipelineConfig config)
    {
        Config = config;
        var forwarding = config.Hazards == HazardHandling.Forwarding;
        var decidesInDecode = config.Branches == BranchDecision.Decode;

        // ---- IF ---------------------------------------------------------------------------
        Add("pc-mux", PartKind.Mux, Stage.Fetch, 16, 150, 12, 70);
        Add("pc", PartKind.Register, Stage.Fetch, 40, 160, 16, 50, "PC");
        Add("pc-add", PartKind.Adder, Stage.Fetch, 72, 84, 30, 30, "+4");
        Add("imem", PartKind.Store, Stage.Fetch, 72, 160, 44, 60, "IMEM");
        Add("if-id", PartKind.Latch, Stage.Fetch, 126, 50, 10, 280, "IF/ID");

        Connect("mux-pc", "pc-mux", "pc", Signal.NextPc, 28, 185, 40, 185);
        Connect("pc-imem", "pc", "imem", Signal.Pc, 56, 190, 72, 190);
        Connect("pc-add", "pc", "pc-add", Signal.Pc, 56, 175, 66, 175, 66, 99, 72, 99);
        Connect("pc-ifid", "pc", "if-id", Signal.Pc, 56, 168, 60, 168, 60, 130, 126, 130);
        Connect("imem-ifid", "imem", "if-id", Signal.Instruction, 116, 190, 126, 190);
        Connect("add-mux", "pc-add", "pc-mux", Signal.PcPlus4, 102, 99, 110, 99, 110, 44, 8, 44, 8, 162, 16, 162);

        // A predictor that always says "straight on" is the adder, and is not drawn twice.
        if (config.Predictor != Predictor.NotTaken)
        {
            Add("predictor", PartKind.Unit, Stage.Fetch, 72, 250, 44, 26, "PREDICT");
            Connect("pc-pred", "pc", "predictor", Signal.Pc, 56, 200, 64, 200, 64, 263, 72, 263);
            Connect("pred-mux", "predictor", "pc-mux", Signal.Predicted, 94, 276, 94, 292, 8, 292, 8, 185, 16, 185);
        }

        // ---- ID ---------------------------------------------------------------------------
        Add("regs", PartKind.Store, Stage.Decode, 176, 135, 60, 95, "REGS");
        Add("imm", PartKind.Logic, Stage.Decode, 224, 262, 48, 26, "IMM");
        Add("id-ex", PartKind.Latch, Stage.Decode, 296, 50, 10, 280, "ID/EX");

        Connect("id-rs1", "if-id", "regs", Signal.Rs1, 136, 160, 176, 160);
        Connect("id-rs2", "if-id", "regs", Signal.Rs2, 136, 185, 176, 185);
        Connect("id-inst", "if-id", "imm", Signal.Instruction, 136, 275, 224, 275);
        Connect("id-pc", "if-id", "id-ex", Signal.Pc, 136, 126, 296, 126);
        Connect("id-a", "regs", "id-ex", Signal.Rs1Value, 236, 150, 296, 150);
        Connect("id-b", "regs", "id-ex", Signal.Rs2Value, 236, 215, 296, 215);
        Connect("id-imm", "imm", "id-ex", Signal.Imm, 272, 275, 296, 275);

        // Something has to hold the pipeline whenever an instruction can be made to wait: for a
        // value, or for a multiplier.
        if (config.Hazards != HazardHandling.Off || config.MulDivCycles > 1)
        {
            Add("hazard", PartKind.Unit, Stage.Decode, 170, 58, 72, 22, "HAZARD");
            Control("hazard-pc", "hazard", "pc", Signal.Hold, 180, 58, 180, 36, 48, 36, 48, 160);
            Control("hazard-ifid", "hazard", "if-id", Signal.Hold, 200, 58, 200, 42, 131, 42, 131, 50);
        }

        if (decidesInDecode)
        {
            Add("cmp", PartKind.Unit, Stage.Decode, 244, 92, 40, 28, "BRANCH");
            Connect("cmp-a", "regs", "cmp", Signal.Rs1Value, 236, 142, 256, 142, 256, 120);
            Connect("cmp-b", "regs", "cmp", Signal.Rs2Value, 236, 223, 268, 223, 268, 120);
            Connect("redirect-id", "cmp", "pc-mux", Signal.Target, 262, 92, 262, 20, 4, 20, 4, 208, 16, 208);
            if (forwarding)
            {
                Connect("cmp-forward", "ex-mem", "cmp", Signal.Forwarded, 486, 290, 494, 290, 494, 336, 290, 336, 290, 106, 284, 106);
            }
        }

        // ---- EX ---------------------------------------------------------------------------
        Add("src-a", PartKind.Mux, Stage.Execute, 358, 116, 12, 48);
        Add("src-b", PartKind.Mux, Stage.Execute, 358, 201, 12, 48);
        Add("alu", PartKind.Alu, Stage.Execute, 392, 118, 50, 124, "ALU");
        Add("ex-mem", PartKind.Latch, Stage.Execute, 476, 50, 10, 280, "EX/MEM");

        Connect("ex-pc", "id-ex", "src-a", Signal.Pc, 306, 112, 352, 112, 352, 128, 358, 128);
        Connect("ex-imm", "id-ex", "src-b", Signal.Imm, 306, 275, 352, 275, 352, 237, 358, 237);
        Connect("alu-a", "src-a", "alu", Signal.AluA, 370, 140, 392, 140);
        Connect("alu-b", "src-b", "alu", Signal.AluB, 370, 225, 392, 225);
        Connect("alu-out", "alu", "ex-mem", Signal.Result, 442, 180, 476, 180);

        // The operands come out of ID/EX. With forwarding each goes through a multiplexer that
        // can take a newer value in its place, and whatever uses the operand is fed from there.
        var operands = forwarding ? ("fwd-a", "fwd-b", 338) : ("id-ex", "id-ex", 306);
        if (forwarding)
        {
            Add("fwd-a", PartKind.Mux, Stage.Execute, 326, 126, 12, 48);
            Add("fwd-b", PartKind.Mux, Stage.Execute, 326, 191, 12, 48);
            Add("forward", PartKind.Unit, Stage.Execute, 372, 290, 60, 22, "FORWARD");

            Connect("ex-rs1", "id-ex", "fwd-a", Signal.Rs1Value, 306, 150, 326, 150);
            Connect("ex-rs2", "id-ex", "fwd-b", Signal.Rs2Value, 306, 215, 326, 215);
            Connect("fwd-a-out", "fwd-a", "src-a", Signal.Rs1Value, 338, 150, 358, 150);
            Connect("fwd-b-out", "fwd-b", "src-b", Signal.Rs2Value, 338, 215, 358, 215);
            Connect("store-data", "fwd-b", "ex-mem", Signal.Rs2Value, 338, 215, 346, 215, 346, 258, 476, 258);

            Connect("fwd-exmem-a", "ex-mem", "fwd-a", Signal.Forwarded, 486, 290, 494, 290, 494, 336, 320, 336, 320, 136, 326, 136);
            Connect("fwd-exmem-b", "ex-mem", "fwd-b", Signal.Forwarded, 486, 290, 494, 290, 494, 336, 320, 336, 320, 201, 326, 201);
            Connect("fwd-memwb-a", "wb-mux", "fwd-a", Signal.Forwarded, 648, 170, 680, 170, 680, 352, 314, 352, 314, 164, 326, 164);
            Connect("fwd-memwb-b", "wb-mux", "fwd-b", Signal.Forwarded, 648, 170, 680, 170, 680, 352, 314, 352, 314, 229, 326, 229);
            Control("forward-a", "forward", "fwd-a", Signal.Select, 380, 290, 380, 183, 332, 183, 332, 174);
            Control("forward-b", "forward", "fwd-b", Signal.Select, 372, 301, 332, 301, 332, 239);
        }
        else
        {
            Connect("ex-rs1", "id-ex", "src-a", Signal.Rs1Value, 306, 150, 358, 150);
            Connect("ex-rs2", "id-ex", "src-b", Signal.Rs2Value, 306, 215, 358, 215);
            Connect("store-data", "id-ex", "ex-mem", Signal.Rs2Value, 306, 258, 476, 258);
        }

        if (!decidesInDecode)
        {
            var (a, b, x) = operands;
            Add("branch", PartKind.Unit, Stage.Execute, 396, 60, 44, 22, "BRANCH");
            Connect("branch-a", a, "branch", Signal.Rs1Value, x, 150, 342, 150, 342, 66, 396, 66);
            Connect("branch-b", b, "branch", Signal.Rs2Value, x, 215, 346, 215, 346, 76, 396, 76);
            Connect("redirect-ex", "branch", "pc-mux", Signal.Target, 418, 60, 418, 20, 4, 20, 4, 208, 16, 208);
        }

        // ---- MEM --------------------------------------------------------------------------
        Add("dmem", PartKind.Store, Stage.Memory, 514, 150, 60, 90, "DMEM");
        Add("commit", PartKind.Unit, Stage.Memory, 510, 60, 60, 22, "COMMIT");
        Add("mem-wb", PartKind.Latch, Stage.Memory, 596, 50, 10, 280, "MEM/WB");

        Connect("mem-address", "ex-mem", "dmem", Signal.Address, 486, 180, 514, 180);
        Connect("mem-data", "ex-mem", "dmem", Signal.WriteData, 486, 258, 502, 258, 502, 225, 514, 225);
        Connect("mem-read", "dmem", "mem-wb", Signal.ReadData, 574, 195, 596, 195);
        Connect("mem-result", "ex-mem", "mem-wb", Signal.Result, 486, 140, 596, 140);
        Connect("redirect-mem", "commit", "pc-mux", Signal.Target, 540, 60, 540, 20, 4, 20, 4, 208, 16, 208);

        // ---- WB ---------------------------------------------------------------------------
        Add("wb-mux", PartKind.Mux, Stage.WriteBack, 636, 130, 12, 80);

        Connect("wb-result", "mem-wb", "wb-mux", Signal.Result, 606, 140, 636, 140);
        Connect("wb-mem", "mem-wb", "wb-mux", Signal.ReadData, 606, 195, 636, 195);
        Connect("wb-write", "wb-mux", "regs", Signal.RdValue, 648, 170, 680, 170, 680, 352, 206, 352, 206, 230);
    }

    /// <summary>How the pipeline drawn is built.</summary>
    public PipelineConfig Config { get; }

    public IReadOnlyCollection<Part> Parts => _parts.Values;

    public IReadOnlyList<Wire> Wires => _wires;

    /// <summary>The part with an id; the ids of a wire's two ends are always those of parts.</summary>
    public Part Part(string id) => _parts[id];

    public bool Has(string part) => _parts.ContainsKey(part);

    /// <summary>The datapath of a pipeline built a given way.</summary>
    public static DatapathModel For(PipelineConfig config)
    {
        config.Validate();
        return new DatapathModel(config);
    }

    /// <summary>
    /// The stretch of the drawing a stage has to itself, from one latch to the next: where to
    /// write what is in the stage.
    /// </summary>
    public (int From, int To) Column(Stage stage) => stage switch
    {
        Stage.Fetch => (0, _parts["if-id"].X),
        Stage.Decode => (_parts["if-id"].Right, _parts["id-ex"].X),
        Stage.Execute => (_parts["id-ex"].Right, _parts["ex-mem"].X),
        Stage.Memory => (_parts["ex-mem"].Right, _parts["mem-wb"].X),
        _ => (_parts["mem-wb"].Right, Width),
    };

    private void Add(string id, PartKind kind, Stage stage, int x, int y, int width, int height, string label = "") =>
        _parts.Add(id, new Part(id, kind, stage, x, y, width, height, label));

    private void Connect(string id, string from, string to, Signal signal, params int[] corners) =>
        Wire(id, from, to, signal, WireKind.Data, corners);

    private void Control(string id, string from, string to, Signal signal, params int[] corners) =>
        Wire(id, from, to, signal, WireKind.Control, corners);

    private void Wire(string id, string from, string to, Signal signal, WireKind kind, int[] corners)
    {
        var points = new Point[corners.Length / 2];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Point(corners[2 * i], corners[2 * i + 1]);
        }

        _wires.Add(new Wire(id, from, to, signal, kind, points));
    }
}
