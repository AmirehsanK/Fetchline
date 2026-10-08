namespace Fetchline.Core.Pipeline;

/// <summary>What the pipeline does about an instruction that needs a value still on its way.</summary>
public enum HazardHandling : byte
{
    /// <summary>
    /// Take the value from the latch it sits in. Only a use straight after a load has to wait,
    /// and then for one cycle.
    /// </summary>
    Forwarding,

    /// <summary>
    /// No forwarding paths: the instruction waits in ID until the one that produces its value
    /// has reached WB. Always right, and slower.
    /// </summary>
    StallOnly,

    /// <summary>
    /// Nothing at all: an instruction takes whatever the register file held when it was in ID.
    /// The program computes a wrong answer, on purpose, to show what the hazard logic is for.
    /// Branches and system instructions still flush what is behind them; only data hazards are
    /// left unhandled.
    /// </summary>
    Off,
}

/// <summary>Where a branch or a jump is decided.</summary>
public enum BranchDecision : byte
{
    /// <summary>
    /// In EX, by the ALU, as in Harris and Harris. A taken branch throws away the two
    /// instructions fetched behind it.
    /// </summary>
    Execute,

    /// <summary>
    /// In ID, by a comparator of its own, as in Patterson and Hennessy. Only one instruction is
    /// thrown away, but the operands are needed a stage earlier, so a branch straight after the
    /// instruction that computes its operand has to wait.
    /// </summary>
    Decode,
}

/// <summary>
/// How the pipeline is built: the what-if switches. Two pipelines with different configurations
/// run the same program to the same result, in different numbers of cycles.
/// </summary>
public sealed record PipelineConfig
{
    /// <summary>The configuration the textbooks draw first: forwarding, and branches decided in EX.</summary>
    public static PipelineConfig Default { get; } = new();

    public HazardHandling Hazards { get; init; } = HazardHandling.Forwarding;

    public BranchDecision Branches { get; init; } = BranchDecision.Execute;

    /// <summary>
    /// Whether a pipeline built this way computes what the program says. The one that does not
    /// is the one with its hazard handling switched off.
    /// </summary>
    public bool IsCorrect => Hazards != HazardHandling.Off;
}
