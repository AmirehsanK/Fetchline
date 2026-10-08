using System.Globalization;

namespace Fetchline.Viz.Explain;

/// <summary>
/// Every sentence the visualizer can say, as a method. A language is a class that implements
/// this, so a message that exists in one language and not in another does not compile. Stage
/// names, mnemonics and register names are technical proper nouns and stay as they are.
/// </summary>
public interface IMessages
{
    /// <summary>The label of a stall in the log.</summary>
    string Stall { get; }

    /// <summary>The label of a forward in the log.</summary>
    string Forward { get; }

    /// <summary>The label of a flush in the log.</summary>
    string Flush { get; }

    /// <summary>The label of a trap in the log.</summary>
    string Trap { get; }

    /// <summary>The consumer is held in ID because the load ahead of it has not read its value yet.</summary>
    string LoadUse(string consumer, string register, string producer);

    /// <summary>An operand was taken from a latch: <c>MEM/WB -> EX.A   x4 from lw</c>.</summary>
    string Forwarded(string latch, string operand, string register, string producer);

    /// <summary>A branch or jump was taken and what fetch had brought in behind it is thrown away.</summary>
    string TakenBranch(string branch, string target, int squashed);

    /// <summary>A system instruction took effect and what is behind it is fetched again.</summary>
    string SystemFlush(string instruction, int squashed);

    /// <summary>An instruction trapped.</summary>
    string Trapped(string instruction, string cause, string handler, int squashed);

    /// <summary>An instruction stopped the machine.</summary>
    string Stopped(string instruction, int squashed);

    /// <summary>The totals of a run: <c>3 instructions, 8 cycles, CPI 2.67, 1 stall, 2 forwards</c>.</summary>
    string Summary(ulong instructions, ulong cycles, double cpi, int stalls, int forwards, int flushes);

    /// <summary>What the mark of a squashed instruction means.</summary>
    string SquashedLegend(string mark);

    /// <summary>The diagram shows only some of the cycles.</summary>
    string MoreCycles(ulong shownFrom, ulong shownTo, ulong total);
}

public sealed class EnglishMessages : IMessages
{
    public static EnglishMessages Instance { get; } = new();

    public string Stall => "stall";

    public string Forward => "forward";

    public string Flush => "flush";

    public string Trap => "trap";

    public string LoadUse(string consumer, string register, string producer) =>
        $"load-use: {consumer} (ID) needs {register}; {producer} (EX) has it only after MEM";

    public string Forwarded(string latch, string operand, string register, string producer) =>
        $"{latch} -> EX.{operand}   {register} from {producer}";

    public string TakenBranch(string branch, string target, int squashed) =>
        $"{branch} (EX) is taken to {target}; {Count(squashed, "instruction")} behind it {(squashed == 1 ? "is" : "are")} squashed";

    public string SystemFlush(string instruction, int squashed) =>
        $"{instruction} (MEM) is a system instruction; {Count(squashed, "instruction")} behind it {(squashed == 1 ? "is" : "are")} fetched again";

    public string Trapped(string instruction, string cause, string handler, int squashed) =>
        $"{instruction} (MEM) raises {cause}; control goes to {handler}, {Count(squashed, "instruction")} squashed";

    public string Stopped(string instruction, int squashed) =>
        $"{instruction} (MEM) stops the machine; {Count(squashed, "instruction")} behind it {(squashed == 1 ? "is" : "are")} squashed";

    public string Summary(ulong instructions, ulong cycles, double cpi, int stalls, int forwards, int flushes)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{Count(instructions, "instruction")}, {Count(cycles, "cycle")}, CPI {cpi:0.00}, {Count(stalls, "stall")}, {Count(forwards, "forward")}");
        return flushes == 0 ? text : text + ", " + Count(flushes, "flush", "flushes");
    }

    public string SquashedLegend(string mark) => $"{mark} squashed: fetched, then thrown away";

    public string MoreCycles(ulong shownFrom, ulong shownTo, ulong total) => string.Create(
        CultureInfo.InvariantCulture,
        $"cycles {shownFrom} to {shownTo} of {total}; --from and --cycles show the rest");

    private static string Count(long number, string one, string? many = null) => string.Create(
        CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many ?? one + "s")}");

    private static string Count(ulong number, string one, string? many = null) => Count((long)number, one, many);
}
