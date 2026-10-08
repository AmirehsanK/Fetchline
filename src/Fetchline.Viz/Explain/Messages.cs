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

    /// <summary>With no forwarding, the consumer is held in ID until its producer reaches WB.</summary>
    string DataHazard(string consumer, string register, string producer, string producerStage);

    /// <summary>A branch decided in ID is held there until its operand has been computed.</summary>
    string BranchOperand(string branch, string register, string producer, string producerStage, string readyAfter);

    /// <summary>An operand was taken from a latch: <c>MEM/WB -> EX.A   x4 from lw</c>.</summary>
    string Forwarded(string latch, string stage, string operand, string register, string producer);

    /// <summary>A branch or jump was taken and what fetch had brought in behind it is thrown away.</summary>
    string TakenBranch(string branch, string stage, string target, int squashed);

    /// <summary>Fetch had been sent after a branch that, when decided, was not taken.</summary>
    string NotTakenBranch(string branch, string stage, int squashed);

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

    /// <summary>What stands where a register would be named, when none was written.</summary>
    string NoRegister { get; }

    /// <summary>What stands where a store would be described, when there was none.</summary>
    string NoStore { get; }

    /// <summary>What stands where an instruction would be named, when the program had ended.</summary>
    string EndOfProgram { get; }

    /// <summary>The first instruction on which the pipeline and the reference machine differed.</summary>
    string FirstWrongValue(ulong index, ulong cycle, string instruction, string detail);

    /// <summary>The two machines wrote different registers or values.</summary>
    string WrongRegister(string pipeline, string reference);

    /// <summary>The two machines stored different things.</summary>
    string WrongStore(string pipeline, string reference);

    /// <summary>The two machines went on to different addresses.</summary>
    string WrongPath(string pipeline, string reference);

    /// <summary>The two machines ran different instructions.</summary>
    string WrongInstruction(string pipeline, string reference);
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

    public string DataHazard(string consumer, string register, string producer, string producerStage) =>
        $"no forwarding: {consumer} (ID) waits for {register} until {producer} ({producerStage}) reaches WB";

    public string BranchOperand(string branch, string register, string producer, string producerStage, string readyAfter) =>
        $"branch in ID: {branch} (ID) needs {register}; {producer} ({producerStage}) has it only after {readyAfter}";

    public string Forwarded(string latch, string stage, string operand, string register, string producer) =>
        $"{latch} -> {stage}.{operand}   {register} from {producer}";

    public string TakenBranch(string branch, string stage, string target, int squashed) =>
        $"{branch} ({stage}) is taken to {target}; {Count(squashed, "instruction")} behind it {(squashed == 1 ? "is" : "are")} squashed";

    public string NotTakenBranch(string branch, string stage, int squashed) =>
        $"{branch} ({stage}) is not taken, but was predicted taken; {Count(squashed, "instruction")} behind it {(squashed == 1 ? "is" : "are")} squashed";

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

    public string NoRegister => "no register";

    public string NoStore => "nothing";

    public string EndOfProgram => "the end of the program";

    public string FirstWrongValue(ulong index, ulong cycle, string instruction, string detail) => string.Create(
        CultureInfo.InvariantCulture,
        $"first wrong value: instruction {index}, {instruction}, in cycle {cycle}: {detail}");

    public string WrongRegister(string pipeline, string reference) =>
        $"the pipeline wrote {pipeline}, the reference machine wrote {reference}";

    public string WrongStore(string pipeline, string reference) =>
        $"the pipeline stored {pipeline}, the reference machine stored {reference}";

    public string WrongPath(string pipeline, string reference) =>
        $"the pipeline went on to {pipeline}, the reference machine to {reference}";

    public string WrongInstruction(string pipeline, string reference) =>
        $"the pipeline ran {pipeline} where the reference machine ran {reference}";

    private static string Count(long number, string one, string? many = null) => string.Create(
        CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many ?? one + "s")}");

    private static string Count(ulong number, string one, string? many = null) => Count((long)number, one, many);
}
