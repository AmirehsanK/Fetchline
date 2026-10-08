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

/// <summary>How fetch guesses where to go after a branch, before the branch is decided.</summary>
public enum Predictor : byte
{
    /// <summary>Always straight on. Every taken branch and every jump is a wrong guess.</summary>
    NotTaken,

    /// <summary>
    /// A fixed rule read off the instruction: a conditional branch backwards is taken, one
    /// forwards is not, and a <c>jal</c> goes where it says.
    /// </summary>
    BackwardTaken,

    /// <summary>A branch target buffer whose entries expect a branch to do what it did last time.</summary>
    OneBit,

    /// <summary>
    /// A branch target buffer with a two-bit saturating counter in each entry, which takes two
    /// wrong guesses in a row to change its mind.
    /// </summary>
    TwoBit,
}

/// <summary>
/// How the pipeline is built: the what-if switches. Two pipelines with different configurations
/// run the same program to the same result, in different numbers of cycles.
/// </summary>
public sealed record PipelineConfig
{
    /// <summary>The longest a multiply or divide can be told to take.</summary>
    public const int MaxMulDivCycles = 64;

    /// <summary>The configuration the textbooks draw first: forwarding, and branches decided in EX.</summary>
    public static PipelineConfig Default { get; } = new();

    public HazardHandling Hazards { get; init; } = HazardHandling.Forwarding;

    public BranchDecision Branches { get; init; } = BranchDecision.Execute;

    public Predictor Predictor { get; init; } = Predictor.NotTaken;

    /// <summary>
    /// How many entries the branch target buffer of a one-bit or two-bit predictor has: a power
    /// of two. The playground offers 16, 64 and 256.
    /// </summary>
    public int BtbEntries { get; init; } = 64;

    /// <summary>
    /// How many cycles a multiply or a divide spends in EX. One is the pipeline of the textbooks,
    /// whose ALU does anything in a cycle; a real multiplier takes a few, and a divider that
    /// produces one bit of the quotient at a time takes thirty-two or so. While it works,
    /// everything behind it waits.
    /// </summary>
    public int MulDivCycles { get; init; } = 1;

    /// <summary>
    /// Throws if the switches cannot be built: a buffer size that is not a power of two, or a
    /// multiplier that takes no time at all.
    /// </summary>
    public void Validate()
    {
        if (BtbEntries is < 1 or > 65536 || (BtbEntries & (BtbEntries - 1)) != 0)
        {
            throw new ArgumentException(
                $"A branch target buffer has a power-of-two number of entries up to 65536, not {BtbEntries}.");
        }

        if (MulDivCycles is < 1 or > MaxMulDivCycles)
        {
            throw new ArgumentException(
                $"A multiply or divide takes from 1 to {MaxMulDivCycles} cycles, not {MulDivCycles}.");
        }
    }

    /// <summary>
    /// Whether a pipeline built this way computes what the program says. The one that does not
    /// is the one with its hazard handling switched off.
    /// </summary>
    public bool IsCorrect => Hazards != HazardHandling.Off;
}
