using Fetchline.Core.Isa;

namespace Fetchline.Core.Trace;

/// <summary>Why a machine stopped.</summary>
public enum StopReason : byte
{
    /// <summary>It has not stopped.</summary>
    None,

    /// <summary>The program asked to exit; <see cref="Commit.ExitCode"/> is its code.</summary>
    Exit,

    /// <summary>Execution ran off the end of the code, which ends a program cleanly.</summary>
    EndOfProgram,

    /// <summary>An <c>ebreak</c> paused the program. It can be resumed.</summary>
    Breakpoint,

    /// <summary>The program did something it cannot do; <see cref="Commit.Message"/> says what.</summary>
    Fault,

    /// <summary>A test program wrote its verdict to <c>tohost</c>; <see cref="Commit.ExitCode"/> is the word.</summary>
    Tohost,
}

/// <summary>
/// Everything one instruction did to the machine, as seen from outside it: the register it
/// wrote, the memory it stored to, whether it trapped, where control went next, and whether the
/// machine stopped. Both machines produce one for every instruction that commits, in program
/// order, and the two streams must be equal. That equality is the lockstep check.
/// </summary>
public readonly record struct Commit
{
    /// <summary>The address of the instruction.</summary>
    public uint Pc { get; init; }

    /// <summary>The instruction; illegal with a zero word when nothing could be fetched.</summary>
    public Instruction Instruction { get; init; }

    /// <summary>The register written, or zero when none was (a write to <c>x0</c> is none).</summary>
    public byte Register { get; init; }

    /// <summary>What was written to <see cref="Register"/>.</summary>
    public uint Value { get; init; }

    /// <summary>How many bytes were stored: 1, 2 or 4, or zero for no store.</summary>
    public byte StoreBytes { get; init; }

    public uint StoreAddress { get; init; }

    /// <summary>The value stored, truncated to <see cref="StoreBytes"/>.</summary>
    public uint StoreValue { get; init; }

    /// <summary>The instruction raised an exception that was delivered to the trap vector.</summary>
    public bool Trapped { get; init; }

    /// <summary>The exception's cause, as written to <c>mcause</c>.</summary>
    public uint Cause { get; init; }

    /// <summary>What was written to <c>mtval</c>: a faulting address or instruction word.</summary>
    public uint TrapValue { get; init; }

    /// <summary>The address of the instruction that runs next.</summary>
    public uint NextPc { get; init; }

    public StopReason Stop { get; init; }

    /// <summary>The exit code of an <see cref="StopReason.Exit"/>, or the word of a <see cref="StopReason.Tohost"/>.</summary>
    public int ExitCode { get; init; }

    /// <summary>What went wrong, for a <see cref="StopReason.Fault"/>.</summary>
    public string? Message { get; init; }

    public bool WritesRegister => Register != 0;

    public bool WritesMemory => StoreBytes != 0;
}
