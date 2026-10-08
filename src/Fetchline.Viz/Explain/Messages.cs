using System.Globalization;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Datapath;

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

    /// <summary>The label of the first wrong value in the log.</summary>
    string Wrong { get; }

    /// <summary>What the log says when a run has had nothing to report.</summary>
    string NothingHappened { get; }

    /// <summary>The consumer is held in ID because the load ahead of it has not read its value yet.</summary>
    string LoadUse(string consumer, string register, string producer);

    /// <summary>With no forwarding, the consumer is held in ID until its producer reaches WB.</summary>
    string DataHazard(string consumer, string register, string producer, string producerStage);

    /// <summary>A branch decided in ID is held there until its operand has been computed.</summary>
    string BranchOperand(string branch, string register, string producer, string producerStage, string readyAfter);

    /// <summary>A multiply or divide keeps EX for another cycle, and what is behind it waits.</summary>
    string MultiCycle(string instruction, string stage, int remaining);

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

    /// <summary>In the playground's legend, beside a stage drawn as held: what that means.</summary>
    string HeldLegend { get; }

    /// <summary>In the playground's legend, beside a stage drawn as squashed.</summary>
    string ThrownAwayLegend { get; }

    /// <summary>In the playground's legend, beside the line of a forward.</summary>
    string ForwardedLegend { get; }

    /// <summary>The names of the playground's panes, as their titles.</summary>
    string SourceTitle { get; }

    string PipelineTitle { get; }

    string LogTitle { get; }

    string RegistersTitle { get; }

    string MemoryTitle { get; }

    string ConsoleTitle { get; }

    string CountersTitle { get; }

    /// <summary>The name of the menu of example programs, which is also what it shows.</summary>
    string ExamplesMenu { get; }

    /// <summary>What the console says while the program has printed nothing.</summary>
    string NothingPrinted { get; }

    /// <summary>What the memory pane says when there is no program to have any.</summary>
    string NoMemory { get; }

    /// <summary>A problem under the source: what is wrong, and what might put it right.</summary>
    string Problem(string message, string? hint);

    /// <summary>Under the problems that are listed, how many more there are.</summary>
    string MoreProblems(int count);

    /// <summary>The names on the soft keys.</summary>
    string RunKey { get; }

    string PauseKey { get; }

    string StepKey { get; }

    string BackKey { get; }

    string ResetKey { get; }

    /// <summary>The speed key: its name with the pace it is set to, or with the word for flat out.</summary>
    string SpeedKey(string pace);

    /// <summary>The key that shows the comparison in place of the diagram, and the one that brings the diagram back.</summary>
    string CompareKey { get; }

    string DiagramKey { get; }

    /// <summary>The title of the pane the comparison is shown in.</summary>
    string CompareTitle { get; }

    /// <summary>Under a comparison that is still being made: how far it has got.</summary>
    string Comparing(int done, int total);

    /// <summary>Under a finished comparison: what its rows are for.</summary>
    string CompareHint { get; }

    /// <summary>The key that shows the datapath in place of the diagram, and the one that brings the diagram back.</summary>
    string DatapathKey { get; }

    string StaircaseKey { get; }

    /// <summary>The title of the pane the datapath is drawn in.</summary>
    string DatapathTitle { get; }

    /// <summary>What a wire of the datapath carries, as a datapath is labelled.</summary>
    string NameOf(Signal signal);

    /// <summary>On a wire that is in use this cycle: what it carries and the value on it.</summary>
    string WireCarries(string signal, string value);

    /// <summary>On a wire that is in use this cycle and carries a decision, not a value.</summary>
    string WireAsserted(string signal);

    /// <summary>On a wire that nothing is using this cycle.</summary>
    string WireIdle(string signal);

    /// <summary>Under the datapath: how to read it.</summary>
    string DatapathHint { get; }

    /// <summary>The key that makes a link to what is on screen.</summary>
    string ShareKey { get; }

    /// <summary>Said when a link has been made and copied.</summary>
    string LinkCopied { get; }

    /// <summary>Said when a link has been made but the browser would not let it be copied.</summary>
    string LinkInAddress { get; }

    /// <summary>Said when what is on screen cannot be put into a link.</summary>
    string LinkNotMade { get; }

    /// <summary>Said when the page was opened with a link it could not read, and why not.</summary>
    string LinkNotRead(LinkProblem problem);

    /// <summary>What the timeline is, for a reader who cannot see it: the control that goes to any cycle run so far.</summary>
    string Timeline { get; }

    /// <summary>At the end of the timeline: how many cycles have been run.</summary>
    string TimelineRun(ulong cycles);

    /// <summary>What the soft keys are, for a reader who cannot see them as a row.</summary>
    string Controls { get; }

    /// <summary>On the status line: the cycle on screen.</summary>
    string StatusCycle(ulong cycle);

    /// <summary>On the status line, before the first cycle.</summary>
    string StatusReady { get; }

    string StatusRunning { get; }

    /// <summary>On the status line when the run has reached the end of its code.</summary>
    string StatusEnded { get; }

    /// <summary>On the status line when the program has left through an exit call.</summary>
    string StatusExited(int code);

    /// <summary>On the status line at an <c>ebreak</c>, where the run waits to be told to go on.</summary>
    string StatusPaused { get; }

    /// <summary>On the status line when the machine has stopped the program, and why.</summary>
    string StatusStopped(string reason);

    /// <summary>On the status line when the source does not assemble.</summary>
    string StatusNothingToRun { get; }

    /// <summary>On the status line once the pipeline has computed something the reference machine did not.</summary>
    string StatusWrong(ulong cycle);

    /// <summary>What the pipeline pane says before the first cycle has run.</summary>
    string ReadyToRun { get; }

    /// <summary>What the pipeline pane says when the source does not assemble.</summary>
    string NothingToRun { get; }

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

    /// <summary>The heading of the comparison table's column for how data hazards are handled.</summary>
    string HazardsHeading { get; }

    /// <summary>The heading of the column for the stage that decides branches.</summary>
    string BranchHeading { get; }

    /// <summary>The heading of the column for the predictor.</summary>
    string PredictorHeading { get; }

    /// <summary>The name of the switch for the size of the branch target buffer.</summary>
    string BufferHeading { get; }

    /// <summary>The name of the switch for how many cycles a multiply or divide takes.</summary>
    string MultiplyHeading { get; }

    /// <summary>What the row of switches is, for a reader who cannot see it as a row.</summary>
    string Switches { get; }

    string CyclesHeading { get; }

    string CpiHeading { get; }

    /// <summary>The heading of the column for cycles in which an instruction was made to wait.</summary>
    string StallsHeading { get; }

    /// <summary>The heading of the column for instructions fetched and then thrown away.</summary>
    string SquashedHeading { get; }

    /// <summary>The heading of the column for branches and jumps that fetch had not followed.</summary>
    string WrongGuessesHeading { get; }

    /// <summary>How many of the branches and jumps decided were guessed wrong: <c>9 of 10</c>.</summary>
    string Guesses(int wrong, int decided);

    /// <summary>The label of the counter of instructions that completed.</summary>
    string InstructionsHeading { get; }

    /// <summary>The label of the counter of redirects that threw something away.</summary>
    string FlushesHeading { get; }

    /// <summary>The label of the counter of operands taken from a latch.</summary>
    string ForwardsHeading { get; }

    /// <summary>The label of the counter of branches and jumps decided.</summary>
    string BranchesHeading { get; }

    /// <summary>The label of the counter of those that were taken.</summary>
    string TakenHeading { get; }

    string LoadsHeading { get; }

    string StoresHeading { get; }

    string TrapsHeading { get; }

    /// <summary>What a stall is called by its cause, in a list of counters.</summary>
    string NameOf(StallCause cause);

    /// <summary>What a flush is called by its cause, in a list of counters.</summary>
    string NameOf(FlushCause cause);

    /// <summary>What marks a row whose run differed from the reference machine.</summary>
    string WrongAnswer { get; }

    /// <summary>What marks a row whose run was cut off before it ended.</summary>
    string CutOff(ulong cycles);

    /// <summary>How long the program is, and the configuration that ran it right in the fewest cycles.</summary>
    string FewestCycles(ulong instructions, string configuration, ulong cycles);

    /// <summary>Where a configuration that computes the wrong answer first went wrong.</summary>
    string WrongWith(string configuration, string firstWrongValue);
}

public sealed class EnglishMessages : IMessages
{
    public static EnglishMessages Instance { get; } = new();

    public string Stall => "stall";

    public string Forward => "forward";

    public string Flush => "flush";

    public string Trap => "trap";

    public string Wrong => "wrong";

    public string NothingHappened => "No stalls, forwards or flushes yet.";

    public string LoadUse(string consumer, string register, string producer) =>
        $"load-use: {consumer} (ID) needs {register}; {producer} (EX) has it only after MEM";

    public string DataHazard(string consumer, string register, string producer, string producerStage) =>
        $"no forwarding: {consumer} (ID) waits for {register} until {producer} ({producerStage}) reaches WB";

    public string BranchOperand(string branch, string register, string producer, string producerStage, string readyAfter) =>
        $"branch in ID: {branch} (ID) needs {register}; {producer} ({producerStage}) has it only after {readyAfter}";

    public string MultiCycle(string instruction, string stage, int remaining) =>
        $"multi-cycle: {instruction} ({stage}) needs {Count(remaining, "more cycle", "more cycles")}; what is behind it waits";

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

    public string HeldLegend => "kept for another cycle";

    public string ThrownAwayLegend => "fetched, then thrown away";

    public string ForwardedLegend => "a value handed on";

    public string SourceTitle => "SOURCE";

    public string PipelineTitle => "PIPELINE";

    public string LogTitle => "LOG";

    public string RegistersTitle => "REGISTERS";

    public string MemoryTitle => "MEMORY";

    public string ConsoleTitle => "CONSOLE";

    public string CountersTitle => "COUNTERS";

    public string ExamplesMenu => "EXAMPLES";

    public string NothingPrinted => "The program has printed nothing yet.";

    public string NoMemory => "No program, so no memory to show.";

    public string Problem(string message, string? hint) => hint is null ? message : $"{message}; {hint}";

    public string MoreProblems(int count) => $"and {Count(count, "more", "more")}";

    public string RunKey => "RUN";

    public string PauseKey => "PAUSE";

    public string StepKey => "STEP";

    public string BackKey => "BACK";

    public string ResetKey => "RESET";

    public string SpeedKey(string pace) => pace.Length == 0 ? "SPEED MAX" : $"SPEED {pace}";

    public string CompareKey => "COMPARE";

    public string DiagramKey => "DIAGRAM";

    public string CompareTitle => "COMPARE";

    public string Comparing(int done, int total) =>
        string.Create(CultureInfo.InvariantCulture, $"Running it every way: {done} of {total} done.");

    public string CompareHint => "Choose a row to build the pipeline that way.";

    public string DatapathKey => "DATAPATH";

    public string StaircaseKey => "DIAGRAM";

    public string DatapathTitle => "DATAPATH";

    // The words a textbook writes beside the wires.
    public string NameOf(Signal signal) => signal switch
    {
        Signal.Pc => "pc",
        Signal.PcPlus4 => "pc + 4",
        Signal.NextPc => "next pc",
        Signal.Predicted => "predicted next pc",
        Signal.Target => "redirect to",
        Signal.Instruction => "instruction",
        Signal.Rs1 => "rs1",
        Signal.Rs2 => "rs2",
        Signal.Rs1Value => "value of rs1",
        Signal.Rs2Value => "value of rs2",
        Signal.Imm => "immediate",
        Signal.AluA => "ALU operand a",
        Signal.AluB => "ALU operand b",
        Signal.Result => "result",
        Signal.Forwarded => "forwarded value",
        Signal.Address => "address",
        Signal.WriteData => "data to write",
        Signal.ReadData => "data read",
        Signal.RdValue => "value written to rd",
        Signal.Hold => "hold",
        _ => "select the forwarded value",
    };

    public string WireCarries(string signal, string value) => $"{signal} = {value}";

    public string WireAsserted(string signal) => signal;

    public string WireIdle(string signal) => $"{signal}: not used this cycle";

    public string DatapathHint => "Bright wires are the ones in use this cycle. Point at one for its value.";

    public string ShareKey => "SHARE";

    public string LinkCopied => "LINK COPIED: THE SOURCE, THE SWITCHES AND THIS CYCLE";

    public string LinkInAddress => "THE LINK IS IN THE ADDRESS BAR";

    public string LinkNotMade => "TOO MUCH TO PUT IN A LINK";

    public string LinkNotRead(LinkProblem problem) => "THE LINK COULD NOT BE READ: " + problem switch
    {
        LinkProblem.Empty => "THERE IS NOTHING IN IT",
        LinkProblem.TooLong => "IT IS TOO LONG",
        LinkProblem.UnknownKind => "IT IS A KIND THIS PAGE DOES NOT READ",
        LinkProblem.NotText => "WHAT IS IN IT IS NOT TEXT",
        LinkProblem.UnknownSettings => "IT HAS SETTINGS THERE ARE NOT",
        LinkProblem.CutShort => "IT WAS CUT SHORT OR CHANGED ON THE WAY",
        _ => "IT IS DAMAGED",
    };

    public string Timeline => "Cycle on screen";

    public string TimelineRun(ulong cycles) => Count(cycles, "cycle run", "cycles run");

    public string Controls => "Run controls";

    public string StatusCycle(ulong cycle) => string.Create(CultureInfo.InvariantCulture, $"CYCLE {cycle}");

    public string StatusReady => "READY";

    public string StatusRunning => "RUNNING";

    public string StatusEnded => "ENDED";

    public string StatusExited(int code) => string.Create(CultureInfo.InvariantCulture, $"EXIT {code}");

    public string StatusPaused => "PAUSED AT EBREAK";

    public string StatusStopped(string reason) => $"STOPPED: {reason}";

    public string StatusNothingToRun => "NOTHING TO RUN";

    public string StatusWrong(ulong cycle) => string.Create(CultureInfo.InvariantCulture, $"WRONG SINCE CYCLE {cycle}");

    public string ReadyToRun => "RV32IM READY\n\nSTEP runs one cycle. RUN goes on to the end.";

    public string NothingToRun =>
        "The source has to assemble before it can run.\nWhat is wrong with it is listed under the source.";

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

    public string HazardsHeading => "hazards";

    public string BranchHeading => "branch";

    public string PredictorHeading => "predictor";

    public string BufferHeading => "btb";

    public string MultiplyHeading => "muldiv";

    public string Switches => "How the pipeline is built";

    public string CyclesHeading => "cycles";

    public string CpiHeading => "CPI";

    public string StallsHeading => "stalls";

    public string SquashedHeading => "squashed";

    public string WrongGuessesHeading => "wrong guesses";

    public string Guesses(int wrong, int decided) =>
        string.Create(CultureInfo.InvariantCulture, $"{wrong} of {decided}");

    public string InstructionsHeading => "instructions";

    public string FlushesHeading => "flushes";

    public string ForwardsHeading => "forwards";

    public string BranchesHeading => "branches";

    public string TakenHeading => "taken";

    public string LoadsHeading => "loads";

    public string StoresHeading => "stores";

    public string TrapsHeading => "traps";

    // The same words the sentences of the log begin with.
    public string NameOf(StallCause cause) => cause switch
    {
        StallCause.LoadUse => "load-use",
        StallCause.DataHazard => "no forwarding",
        StallCause.BranchOperand => "branch in ID",
        _ => "multi-cycle",
    };

    public string NameOf(FlushCause cause) => cause switch
    {
        FlushCause.Branch => "branch",
        FlushCause.System => "system instruction",
        FlushCause.Trap => "trap",
        _ => "stop",
    };

    public string WrongAnswer => "wrong";

    public string CutOff(ulong cycles) => $"still running after {Count(cycles, "cycle")}";

    public string FewestCycles(ulong instructions, string configuration, ulong cycles) =>
        $"{Count(instructions, "instruction")}; fewest cycles with the right answer: {configuration} ({cycles.ToString(CultureInfo.InvariantCulture)})";

    public string WrongWith(string configuration, string firstWrongValue) => $"{configuration}: {firstWrongValue}";

    private static string Count(long number, string one, string? many = null) => string.Create(
        CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many ?? one + "s")}");

    private static string Count(ulong number, string one, string? many = null) => Count((long)number, one, many);
}
