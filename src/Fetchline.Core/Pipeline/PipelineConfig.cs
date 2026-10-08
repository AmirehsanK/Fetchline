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

/// <summary>
/// How the pipeline is built: the what-if switches. Two pipelines with different configurations
/// run the same program to the same result, in different numbers of cycles.
/// </summary>
public sealed record PipelineConfig
{
    /// <summary>The configuration the textbooks draw first: forwarding, and branches decided in EX.</summary>
    public static PipelineConfig Default { get; } = new();

    public HazardHandling Hazards { get; init; } = HazardHandling.Forwarding;

    /// <summary>
    /// Whether a pipeline built this way computes what the program says. The one that does not
    /// is the one with its hazard handling switched off.
    /// </summary>
    public bool IsCorrect => Hazards != HazardHandling.Off;
}
