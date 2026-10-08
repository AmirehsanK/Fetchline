using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;

namespace Fetchline.Tests.Machine;

/// <summary>Bare metal: CSR instructions, traps and returning from them.</summary>
public class TrapTests
{
    private static readonly AssemblerOptions BareBases = new() { TextBase = 0x8000_0000, DataBase = 0x8000_4000 };

    /// <summary>Runs a program on bare metal until it executes <c>wfi</c> at the label "end", or gives up.</summary>
    private static (ReferenceMachine Machine, List<Commit> Commits) RunBare(string source, int maxInstructions = 10_000)
    {
        var program = AssemblerTesting.Assemble(source + "\nend: j end\n", BareBases);
        var machine = new ReferenceMachine(program, environment: ExecutionEnvironment.Bare);
        var end = program.AddressOf("end");
        var commits = new List<Commit>();

        while (machine.Hart.Pc != end && commits.Count < maxInstructions)
        {
            commits.Add(machine.Step());
        }

        Assert.True(machine.Hart.Pc == end, $"never reached 'end'; stopped at pc 0x{machine.Hart.Pc:x8}");
        return (machine, commits);
    }

    private static uint Reg(ReferenceMachine machine, string name)
    {
        Assert.True(Registers.TryParse(name, out var index));
        return machine.Hart.X[index];
    }

    [Fact]
    public void BareMetalStartsWithEveryRegisterZeroAndNoMap()
    {
        var program = AssemblerTesting.Assemble("nop", BareBases);
        var hart = new ReferenceMachine(program, environment: ExecutionEnvironment.Bare).Hart;

        Assert.All(hart.X, value => Assert.Equal(0u, value));
        Assert.Equal(0x8000_0000u, hart.Pc);
        Assert.Null(hart.Map);
        Assert.Equal(ExecutionEnvironment.Bare, hart.Environment);
    }

    [Fact]
    public void AProgramWithATohostSymbolIsTakenToBeBareMetal()
    {
        var bare = new ReferenceMachine(AssemblerTesting.Assemble("nop\n.data\ntohost: .word 0", BareBases));
        var host = new ReferenceMachine(AssemblerTesting.Assemble("nop\n.data\nother: .word 0"));

        Assert.Equal(ExecutionEnvironment.Bare, bare.Hart.Environment);
        Assert.Equal(ExecutionEnvironment.Host, host.Hart.Environment);
    }

    [Fact]
    public void CsrInstructionsReadTheOldValueAndWriteANewOne()
    {
        var (machine, _) = RunBare("""
            li     t0, 0xF0
            csrrw  a0, mscratch, t0      # a0 = 0,    mscratch = 0xF0
            li     t1, 0x0F
            csrrs  a1, mscratch, t1      # a1 = 0xF0, mscratch = 0xFF
            li     t2, 0x33
            csrrc  a2, mscratch, t2      # a2 = 0xFF, mscratch = 0xCC
            csrrwi a3, mscratch, 21      # a3 = 0xCC, mscratch = 21
            csrrsi a4, mscratch, 8       # a4 = 21,   mscratch = 29
            csrrci a5, mscratch, 5       # a5 = 29,   mscratch = 24
            csrr   a6, mscratch
            """);

        Assert.Equal(0u, Reg(machine, "a0"));
        Assert.Equal(0xF0u, Reg(machine, "a1"));
        Assert.Equal(0xFFu, Reg(machine, "a2"));
        Assert.Equal(0xCCu, Reg(machine, "a3"));
        Assert.Equal(21u, Reg(machine, "a4"));
        Assert.Equal(29u, Reg(machine, "a5"));
        Assert.Equal(24u, Reg(machine, "a6"));
        Assert.Equal(24u, machine.Hart.Csrs.Mscratch);
    }

    [Fact]
    public void ACsrInstructionIsRecordedAsWritingItsRegister()
    {
        var (_, commits) = RunBare("li t0, 7\ncsrw mscratch, t0\ncsrr a0, mscratch");

        Assert.Equal((0, 0u), (commits[1].Register, commits[1].Value));     // csrw: rd is x0
        Assert.Equal((10, 7u), (commits[2].Register, commits[2].Value));
        Assert.All(commits, c => Assert.False(c.Trapped));
    }

    [Fact]
    public void SetAndClearWithAZeroOperandFieldOnlyRead()
    {
        // mhartid is read-only. "csrr" is csrrs with x0, which must not count as a write.
        var (machine, commits) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            csrr  a0, mhartid            # fine: nothing is written
            csrrs a1, mhartid, zero
            csrrc a2, mhartid, zero
            csrrsi a3, mhartid, 0
            li    a7, 1
            j     end
            handler:
            li    a7, 99
            """);

        Assert.Equal(1u, Reg(machine, "a7"));
        Assert.DoesNotContain(commits, c => c.Trapped);
    }

    [Theory]
    [InlineData("csrw  mhartid, t0", "csrrw: any write to a read-only register")]
    [InlineData("csrrw a0, mhartid, zero", "csrrw always writes, even from x0")]
    [InlineData("csrs  mhartid, t0", "csrrs with a real operand register")]
    [InlineData("csrsi cycle, 1", "csrrsi with a non-zero constant")]
    [InlineData("csrr  a0, satp", "a register that does not exist")]
    [InlineData("csrwi 0x744, 8", "mnstatus, as the official start-up code probes it")]
    [InlineData("csrr  a0, time", "there is no timer")]
    public void AnAccessTheCoreCannotMakeIsAnIllegalInstruction(string instruction, string why)
    {
        var (machine, commits) = RunBare($"""
            la    t0, handler
            csrw  mtvec, t0
            li    a0, 123
            bad:  {instruction}
            li    a7, 99                 # skipped: the trap goes to the handler
            handler:
            csrr  s0, mcause
            csrr  s1, mepc
            csrr  s2, mtval
            """);

        var trap = Assert.Single(commits, c => c.Trapped);
        Assert.True(trap.Cause == TrapCause.IllegalInstruction, why);
        Assert.Equal(trap.Instruction.Raw, trap.TrapValue);
        Assert.Equal((0, false), (trap.Register, trap.WritesMemory));

        Assert.Equal(2u, Reg(machine, "s0"));
        Assert.Equal(machine.Hart.Program.TryGetSymbol("bad", out var bad) ? bad.Value : 0, Reg(machine, "s1"));
        Assert.Equal(trap.Instruction.Raw, Reg(machine, "s2"));
        Assert.Equal(123u, Reg(machine, "a0"));     // rd was not written by the instruction that trapped
        Assert.Equal(0u, Reg(machine, "a7"));
    }

    [Fact]
    public void EcallTrapsWithTheMachineCauseAndMretComesBack()
    {
        var (machine, commits) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            li    a0, 1
            call_site:
            ecall
            li    a0, 3                  # runs after the handler returns
            j     end
            handler:
            li    a0, 2
            csrr  s0, mcause
            csrr  s1, mepc
            addi  s2, s1, 4              # resume after the ecall, not on it
            csrw  mepc, s2
            mret
            """);

        Assert.Equal(11u, Reg(machine, "s0"));
        Assert.Equal(3u, Reg(machine, "a0"));
        Assert.True(machine.Hart.Program.TryGetSymbol("call_site", out var site));
        Assert.Equal(site.Value, Reg(machine, "s1"));

        var trap = Assert.Single(commits, c => c.Trapped);
        Assert.Equal((TrapCause.EcallFromMachine, 0u, site.Value), (trap.Cause, trap.TrapValue, trap.Pc));

        var back = Assert.Single(commits, c => c.Instruction.Op == Op.Mret);
        Assert.Equal(site.Value + 4, back.NextPc);
        Assert.False(back.Trapped);
    }

    [Fact]
    public void EbreakTrapsWithItsOwnAddress()
    {
        var (machine, commits) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            here: ebreak
            handler:
            csrr  s0, mcause
            csrr  s1, mtval
            """);

        Assert.True(machine.Hart.Program.TryGetSymbol("here", out var here));
        Assert.Equal(3u, Reg(machine, "s0"));
        Assert.Equal(here.Value, Reg(machine, "s1"));
        Assert.Equal(StopReason.None, Assert.Single(commits, c => c.Trapped).Stop);
    }

    [Fact]
    public void AnIllegalInstructionLeavesItsBitsInMtval()
    {
        var (machine, _) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            .word 0xFFFFFFFF
            handler:
            csrr  s0, mcause
            csrr  s1, mtval
            """);

        Assert.Equal((2u, 0xFFFF_FFFFu), (Reg(machine, "s0"), Reg(machine, "s1")));
    }

    [Fact]
    public void AJumpToAnAddressThatIsNotAMultipleOfFourTrapsOnTheJump()
    {
        var (machine, commits) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            la    t1, target + 2
            li    ra, 0x55
            jump: jalr ra, 0(t1)
            target:
            li    a7, 99
            handler:
            csrr  s0, mcause
            csrr  s1, mepc
            csrr  s2, mtval
            """);

        Assert.True(machine.Hart.Program.TryGetSymbol("jump", out var jump));
        Assert.Equal(0u, Reg(machine, "s0"));                       // instruction address misaligned
        Assert.Equal(jump.Value, Reg(machine, "s1"));               // the jump is at fault, not its target
        Assert.Equal(jump.Value + 6, Reg(machine, "s2"));
        Assert.Equal(0x55u, Reg(machine, "ra"));                    // and it did not write its link register
        Assert.Equal(0u, Reg(machine, "a7"));
        Assert.Single(commits, c => c.Trapped);
    }

    [Fact]
    public void ABranchThatIsNotTakenMayPointAnywhere()
    {
        var (machine, commits) = RunBare("li t0, 1\nbeqz t0, . + 6\nli a0, 5");

        Assert.Equal(5u, Reg(machine, "a0"));
        Assert.DoesNotContain(commits, c => c.Trapped);
    }

    [Fact]
    public void ATrapSavesAndRestoresTheInterruptEnable()
    {
        var (machine, _) = RunBare("""
            la    t0, handler
            csrw  mtvec, t0
            csrsi mstatus, 8             # MIE on
            ecall
            csrr  s1, mstatus            # after mret: MIE back on, MPIE set
            j     end
            handler:
            csrr  s0, mstatus            # in the handler: MIE off, MPIE holds the old MIE
            csrr  t1, mepc
            addi  t1, t1, 4
            csrw  mepc, t1
            mret
            """);

        Assert.Equal(0x1880u, Reg(machine, "s0"));
        Assert.Equal(0x1888u, Reg(machine, "s1"));
    }

    [Fact]
    public void InstretCountsInstructionsThatCompleteAndCanBeSet()
    {
        var (machine, _) = RunBare("""
            la    t0, handler            # 2 instructions
            csrw  mtvec, t0              # 3
            csrr  a0, minstret           # reads 3: the three before it
            ecall                        # traps: does not complete, is not counted
            back:
            csrr  a1, minstret           # handler ran 4 instructions: 4 + 4 = 8
            li    t1, 1000
            csrw  minstret, t1           # the next instruction reads exactly this
            csrr  a2, minstret
            csrr  a3, instret            # the unprivileged name, one instruction later
            j     end
            handler:
            csrr  t2, mepc
            addi  t2, t2, 4
            csrw  mepc, t2
            mret
            """);

        Assert.Equal(3u, Reg(machine, "a0"));
        Assert.Equal(8u, Reg(machine, "a1"));
        Assert.Equal(1000u, Reg(machine, "a2"));
        Assert.Equal(1001u, Reg(machine, "a3"));
    }

    [Fact]
    public void MemoryOnBareMetalIsFlatAndWritableEverywhereIncludingTheCode()
    {
        // The program overwrites its own next instruction and then runs the new one.
        var (machine, _) = RunBare("""
            la    t0, patch
            la    t1, replacement
            lw    t2, 0(t1)
            sw    t2, 0(t0)
            fence.i
            patch:
            li    a0, 1                  # replaced before it runs
            j     end
            replacement:
            li    a0, 2
            """);

        Assert.Equal(2u, Reg(machine, "a0"));

        var (far, _) = RunBare("li t0, 0x12340000\nli t1, 77\nsw t1, 0(t0)\nlw a0, 0(t0)\nlw a1, 4(zero)");
        Assert.Equal(77u, Reg(far, "a0"));
        Assert.Equal(0u, Reg(far, "a1"));
    }

    [Fact]
    public void CodeThatRewritesItselfIsReDecodedEvenAfterItHasRun()
    {
        // The loop body runs once as "li a0, 1", is patched, and must run next as "li a0, 2".
        var (machine, _) = RunBare("""
            li    s0, 2
            la    t0, body
            la    t1, replacement
            lw    t2, 0(t1)
            body:
            li    a0, 1
            add   a1, a1, a0
            sw    t2, 0(t0)
            fence.i
            addi  s0, s0, -1
            bnez  s0, body
            j     end
            replacement:
            li    a0, 2
            """);

        Assert.Equal(3u, Reg(machine, "a1"));
    }

    [Fact]
    public void AWriteToTohostEndsTheRunWithTheWordWritten()
    {
        var source = """
            li    gp, 1
            la    t0, tohost
            sw    zero, 0(t0)            # zero is not a verdict: the run goes on
            sw    gp, 0(t0)
            li    a0, 99
            .data
            tohost: .word 0, 0
            """;
        var machine = new ReferenceMachine(AssemblerTesting.Assemble(source, BareBases));

        var last = machine.Run(100);

        Assert.Equal((StopReason.Tohost, 1), (last.Stop, last.ExitCode));
        Assert.True(last.WritesMemory);
        Assert.True(machine.IsFinished);
        Assert.Equal(0u, machine.Hart.X[10]);
    }

    [Fact]
    public void InAHostProgramTheCountersCanBeReadButAnUnknownCsrIsAFault()
    {
        var counted = MachineTesting.RunToEnd("nop\nnop\nrdinstret a0\nrdcycle a1\ncsrr a2, mhartid");
        Assert.Equal(2u, counted["a0"]);
        Assert.Equal(3u, counted["a1"]);
        Assert.Equal(0u, counted["a2"]);

        var bad = MachineTesting.Run("nop\ncsrr a0, satp");
        Assert.Equal(StopReason.Fault, bad.Last.Stop);
        Assert.Equal(
            "an access to CSR satp, which this machine does not have or cannot write, at pc 0x00000004",
            bad.Last.Message);
        Assert.False(bad.Last.Trapped);
    }
}
