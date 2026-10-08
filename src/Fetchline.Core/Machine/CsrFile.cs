using Fetchline.Core.Isa;

namespace Fetchline.Core.Machine;

/// <summary>
/// The control and status registers of a machine-mode-only hart, and what a trap does to them.
///
/// A register that is not here does not exist: reading or writing it is an illegal instruction.
/// That is not an omission to work around. The official tests' start-up code writes <c>satp</c>,
/// <c>pmpaddr0</c>, <c>medeleg</c> and others after pointing the trap vector at the next label,
/// precisely so that a core without those features traps and carries on.
/// </summary>
public sealed class CsrFile
{
    private const uint StatusMie = 1u << 3;
    private const uint StatusMpie = 1u << 7;

    // The previous privilege mode. With only machine mode it can hold nothing but 3.
    private const uint StatusMpp = 3u << 11;

    // RV32 (MXL = 1 in the top two bits) with the I and M extensions.
    private const uint MisaValue = (1u << 30) | (1u << ('I' - 'A')) | (1u << ('M' - 'A'));

    // The three machine interrupt enables. No interrupt is ever raised, but the bits can be set.
    private const uint InterruptBits = (1u << 3) | (1u << 7) | (1u << 11);

    private uint _status;

    /// <summary>How many clock cycles have passed: <c>mcycle</c> and <c>mcycleh</c>.</summary>
    public ulong Cycle { get; set; }

    /// <summary>How many instructions have completed: <c>minstret</c> and <c>minstreth</c>.</summary>
    public ulong InstructionsRetired { get; set; }

    /// <summary>
    /// Set when an instruction writes <c>minstret</c>. The value written is what the next
    /// instruction must read, so the instruction that wrote it does not also count itself.
    /// </summary>
    public bool WroteInstret { get; set; }

    public uint Mstatus => _status | StatusMpp;

    public uint Mie { get; private set; }

    public uint Mtvec { get; private set; }

    public uint Mscratch { get; private set; }

    public uint Mepc { get; private set; }

    public uint Mcause { get; private set; }

    public uint Mtval { get; private set; }

    /// <summary>Reads a register. False when there is no such register.</summary>
    public bool TryRead(int number, out uint value)
    {
        switch (number)
        {
            case Csr.Mstatus:
                value = Mstatus;
                return true;
            case Csr.Misa:
                value = MisaValue;
                return true;
            case Csr.Mie:
                value = Mie;
                return true;
            case Csr.Mtvec:
                value = Mtvec;
                return true;
            case Csr.Mscratch:
                value = Mscratch;
                return true;
            case Csr.Mepc:
                value = Mepc;
                return true;
            case Csr.Mcause:
                value = Mcause;
                return true;
            case Csr.Mtval:
                value = Mtval;
                return true;

            // Nothing is ever pending, the upper half of the status holds nothing this core has,
            // and the identification registers are allowed to say "not given".
            case Csr.Mip or Csr.Mstatush or Csr.Mvendorid or Csr.Marchid or Csr.Mimpid or Csr.Mhartid or Csr.Mconfigptr:
                value = 0;
                return true;

            case Csr.Mcycle or Csr.Cycle:
                value = (uint)Cycle;
                return true;
            case Csr.Mcycleh or Csr.Cycleh:
                value = (uint)(Cycle >> 32);
                return true;
            case Csr.Minstret or Csr.Instret:
                value = (uint)InstructionsRetired;
                return true;
            case Csr.Minstreth or Csr.Instreth:
                value = (uint)(InstructionsRetired >> 32);
                return true;

            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Writes a register. False when there is no such register or it is read-only; a register
    /// that exists but ignores some or all of what is written to it is still a success.
    /// </summary>
    public bool TryWrite(int number, uint value)
    {
        if (Csr.IsReadOnly(number) || !TryRead(number, out _))
        {
            return false;
        }

        switch (number)
        {
            case Csr.Mstatus:
                _status = value & (StatusMie | StatusMpie);
                break;
            case Csr.Mie:
                Mie = value & InterruptBits;
                break;
            case Csr.Mtvec:
                // Direct mode only: the two mode bits stay zero, so every trap goes to the base.
                Mtvec = value & ~3u;
                break;
            case Csr.Mscratch:
                Mscratch = value;
                break;
            case Csr.Mepc:
                // An instruction address is a multiple of four; the low bits cannot be set.
                Mepc = value & ~3u;
                break;
            case Csr.Mcause:
                Mcause = value;
                break;
            case Csr.Mtval:
                Mtval = value;
                break;
            case Csr.Mcycle:
                Cycle = (Cycle & 0xFFFF_FFFF_0000_0000) | value;
                break;
            case Csr.Mcycleh:
                Cycle = (Cycle & 0x0000_0000_FFFF_FFFF) | ((ulong)value << 32);
                break;
            case Csr.Minstret:
                InstructionsRetired = (InstructionsRetired & 0xFFFF_FFFF_0000_0000) | value;
                WroteInstret = true;
                break;
            case Csr.Minstreth:
                InstructionsRetired = (InstructionsRetired & 0x0000_0000_FFFF_FFFF) | ((ulong)value << 32);
                WroteInstret = true;
                break;
        }

        return true;
    }

    /// <summary>
    /// Takes a trap: records where and why, turns interrupts off while remembering whether they
    /// were on, and returns the address of the handler.
    /// </summary>
    public uint EnterTrap(uint pc, uint cause, uint value)
    {
        Mepc = pc;
        Mcause = cause;
        Mtval = value;

        var wasEnabled = (_status & StatusMie) != 0;
        _status = wasEnabled ? StatusMpie : 0;
        return Mtvec;
    }

    /// <summary><c>mret</c>: puts the interrupt enable back and returns the address to resume at.</summary>
    public uint ReturnFromTrap()
    {
        var wasEnabled = (_status & StatusMpie) != 0;
        _status = StatusMpie | (wasEnabled ? StatusMie : 0);
        return Mepc;
    }
}
