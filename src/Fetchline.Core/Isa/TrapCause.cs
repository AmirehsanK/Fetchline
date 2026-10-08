namespace Fetchline.Core.Isa;

/// <summary>The exception causes this core can raise, as they are written to <c>mcause</c>.</summary>
public static class TrapCause
{
    public const uint InstructionAddressMisaligned = 0;
    public const uint InstructionAccessFault = 1;
    public const uint IllegalInstruction = 2;
    public const uint Breakpoint = 3;
    public const uint LoadAddressMisaligned = 4;
    public const uint LoadAccessFault = 5;
    public const uint StoreAddressMisaligned = 6;
    public const uint StoreAccessFault = 7;

    /// <summary>An <c>ecall</c> made in machine mode, which is the only mode there is.</summary>
    public const uint EcallFromMachine = 11;

    /// <summary>A short name for a cause, as <c>riscv-opcodes</c> gives it.</summary>
    public static string Name(uint cause) => cause switch
    {
        InstructionAddressMisaligned => "misaligned fetch",
        InstructionAccessFault => "fetch access",
        IllegalInstruction => "illegal instruction",
        Breakpoint => "breakpoint",
        LoadAddressMisaligned => "misaligned load",
        LoadAccessFault => "load access",
        StoreAddressMisaligned => "misaligned store",
        StoreAccessFault => "store access",
        EcallFromMachine => "machine ecall",
        _ => "cause " + cause,
    };
}
