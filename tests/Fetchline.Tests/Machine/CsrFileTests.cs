using Fetchline.Core.Isa;
using Fetchline.Core.Machine;

namespace Fetchline.Tests.Machine;

public class CsrFileTests
{
    private static uint Read(CsrFile csrs, int number)
    {
        Assert.True(csrs.TryRead(number, out var value), $"{Csr.Format(number)} should exist");
        return value;
    }

    [Theory]
    [InlineData(Csr.Mscratch, 0xDEAD_BEEFu, 0xDEAD_BEEFu)]     // holds anything
    [InlineData(Csr.Mcause, 0x8000_000Bu, 0x8000_000Bu)]
    [InlineData(Csr.Mtval, 0x1234_5678u, 0x1234_5678u)]
    [InlineData(Csr.Mtvec, 0x8000_0103u, 0x8000_0100u)]        // direct mode only: the mode bits stay zero
    [InlineData(Csr.Mepc, 0x8000_0007u, 0x8000_0004u)]         // an instruction address is a multiple of four
    [InlineData(Csr.Mie, 0xFFFF_FFFFu, 0x0000_0888u)]          // only the three machine interrupt enables
    [InlineData(Csr.Mstatus, 0xFFFF_FFFFu, 0x0000_1888u)]      // MIE, MPIE, and MPP stuck at machine
    [InlineData(Csr.Mstatus, 0u, 0x0000_1800u)]
    [InlineData(Csr.Misa, 0u, 0x4000_1100u)]                   // RV32 with I and M, whatever is written
    [InlineData(Csr.Mip, 0xFFFF_FFFFu, 0u)]                    // nothing is ever pending
    [InlineData(Csr.Mstatush, 0xFFFF_FFFFu, 0u)]
    public void AWriteKeepsOnlyTheBitsTheRegisterHas(int number, uint written, uint readBack)
    {
        var csrs = new CsrFile();

        Assert.True(csrs.TryWrite(number, written));

        Assert.Equal(readBack, Read(csrs, number));
    }

    [Theory]
    [InlineData(Csr.Mhartid)]
    [InlineData(Csr.Mvendorid)]
    [InlineData(Csr.Marchid)]
    [InlineData(Csr.Mimpid)]
    [InlineData(Csr.Mconfigptr)]
    [InlineData(Csr.Cycle)]
    [InlineData(Csr.Instret)]
    [InlineData(Csr.Cycleh)]
    [InlineData(Csr.Instreth)]
    public void AReadOnlyRegisterCanBeReadButNotWritten(int number)
    {
        var csrs = new CsrFile();

        Assert.True(csrs.TryRead(number, out _));
        Assert.False(csrs.TryWrite(number, 0));
        Assert.True(Csr.IsReadOnly(number));
    }

    [Theory]
    [InlineData(Csr.Satp)]            // no virtual memory
    [InlineData(Csr.Sstatus)]         // no supervisor mode
    [InlineData(Csr.Medeleg)]         // nothing to delegate to
    [InlineData(Csr.Mideleg)]
    [InlineData(Csr.Mcounteren)]      // no lower mode to hide the counters from
    [InlineData(Csr.Time)]            // no timer
    [InlineData(Csr.Timeh)]
    [InlineData(Csr.Tselect)]         // no debug triggers
    [InlineData(Csr.Tdata1)]
    [InlineData(0x3A0)]               // pmpcfg0: no physical memory protection
    [InlineData(0x3B0)]               // pmpaddr0
    [InlineData(0x744)]               // mnstatus: no resumable non-maskable interrupts
    [InlineData(0x001)]               // fflags: no floating point
    [InlineData(0x7C0)]               // a custom register nobody defined
    [InlineData(0xFFF)]
    public void ARegisterTheCoreLacksDoesNotExist(int number)
    {
        var csrs = new CsrFile();

        Assert.False(csrs.TryRead(number, out var value));
        Assert.Equal(0u, value);
        Assert.False(csrs.TryWrite(number, 1));
    }

    [Fact]
    public void TheCountersAreSixtyFourBitsReadInTwoHalves()
    {
        var csrs = new CsrFile { Cycle = 0x1122_3344_5566_7788, InstructionsRetired = 0xAABB_CCDD_0011_2233 };

        Assert.Equal(0x5566_7788u, Read(csrs, Csr.Mcycle));
        Assert.Equal(0x1122_3344u, Read(csrs, Csr.Mcycleh));
        Assert.Equal(0x0011_2233u, Read(csrs, Csr.Minstret));
        Assert.Equal(0xAABB_CCDDu, Read(csrs, Csr.Minstreth));

        // The unprivileged names read the same counters.
        Assert.Equal(Read(csrs, Csr.Mcycle), Read(csrs, Csr.Cycle));
        Assert.Equal(Read(csrs, Csr.Mcycleh), Read(csrs, Csr.Cycleh));
        Assert.Equal(Read(csrs, Csr.Minstret), Read(csrs, Csr.Instret));
        Assert.Equal(Read(csrs, Csr.Minstreth), Read(csrs, Csr.Instreth));
    }

    [Fact]
    public void WritingHalfACounterLeavesTheOtherHalf()
    {
        var csrs = new CsrFile { Cycle = 0x1111_1111_2222_2222, InstructionsRetired = 0x3333_3333_4444_4444 };

        Assert.True(csrs.TryWrite(Csr.Mcycle, 0xAAAA_AAAA));
        Assert.Equal(0x1111_1111_AAAA_AAAAul, csrs.Cycle);
        Assert.True(csrs.TryWrite(Csr.Mcycleh, 0xBBBB_BBBB));
        Assert.Equal(0xBBBB_BBBB_AAAA_AAAAul, csrs.Cycle);
        Assert.False(csrs.WroteInstret);

        Assert.True(csrs.TryWrite(Csr.Minstreth, 0xCCCC_CCCC));
        Assert.Equal(0xCCCC_CCCC_4444_4444ul, csrs.InstructionsRetired);
        Assert.True(csrs.WroteInstret);
    }

    [Fact]
    public void ATrapRecordsWhereAndWhyAndTurnsInterruptsOff()
    {
        var csrs = new CsrFile();
        csrs.TryWrite(Csr.Mtvec, 0x8000_0100);
        csrs.TryWrite(Csr.Mstatus, 1u << 3);            // interrupts on

        var handler = csrs.EnterTrap(pc: 0x8000_0040, TrapCause.IllegalInstruction, value: 0xFFFF_FFFF);

        Assert.Equal(0x8000_0100u, handler);
        Assert.Equal(0x8000_0040u, csrs.Mepc);
        Assert.Equal(2u, csrs.Mcause);
        Assert.Equal(0xFFFF_FFFFu, csrs.Mtval);
        Assert.Equal(0x0000_1880u, csrs.Mstatus);       // MIE cleared, MPIE holds what it was

        // mret puts the enable back and leaves MPIE set, as the specification says.
        Assert.Equal(0x8000_0040u, csrs.ReturnFromTrap());
        Assert.Equal(0x0000_1888u, csrs.Mstatus);
    }

    [Fact]
    public void ATrapTakenWithInterruptsOffReturnsWithThemOff()
    {
        var csrs = new CsrFile();

        csrs.EnterTrap(0x40, TrapCause.Breakpoint, 0x40);
        Assert.Equal(0x0000_1800u, csrs.Mstatus);

        csrs.ReturnFromTrap();
        Assert.Equal(0x0000_1880u, csrs.Mstatus);       // MIE = old MPIE = 0; MPIE = 1
    }

    [Fact]
    public void ANestedTrapOverwritesTheFirstOnesRecord()
    {
        // There is one mepc. A handler that can trap again has to save it first.
        var csrs = new CsrFile();
        csrs.EnterTrap(0x40, TrapCause.EcallFromMachine, 0);
        csrs.EnterTrap(0x80, TrapCause.IllegalInstruction, 7);

        Assert.Equal((0x80u, 2u, 7u), (csrs.Mepc, csrs.Mcause, csrs.Mtval));
    }
}
