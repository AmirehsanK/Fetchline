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
}
