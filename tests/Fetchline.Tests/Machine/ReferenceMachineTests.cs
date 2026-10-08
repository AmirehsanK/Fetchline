using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Trace;
using static Fetchline.Tests.Machine.MachineTesting;

namespace Fetchline.Tests.Machine;

/// <summary>Programs of a few instructions, and what each instruction did.</summary>
public class ReferenceMachineTests
{
    [Fact]
    public void AMachineStartsWithAStackAGlobalPointerAndSomewhereToReturnTo()
    {
        var machine = new ReferenceMachine(Asm.AssemblerTesting.Assemble("nop\n_start: nop\nnop"));
        var x = machine.Hart.X;

        Assert.Equal(4u, machine.Hart.Pc);                 // _start
        Assert.Equal(0x7FFF_FFF0u, x[2]);                  // sp
        Assert.Equal(0x1000_0000u, x[3]);                  // gp
        Assert.Equal(12u, x[1]);                           // ra: just past the code
        Assert.All(Enumerable.Range(4, 28), i => Assert.Equal(0u, x[i]));
        Assert.Equal(StopReason.None, machine.Stopped);
        Assert.False(machine.IsFinished);
    }

    [Fact]
    public void EveryInstructionLeavesARecordOfWhatItDid()
    {
        var result = RunToEnd("""
            li   a0, 5
            li   a1, 7
            add  a2, a0, a1
            sub  a3, a0, a1
            """);

        Assert.Equal(
            [
                (0x0u, 10, 5u, 0x4u),
                (0x4u, 11, 7u, 0x8u),
                (0x8u, 12, 12u, 0xCu),
                (0xCu, 13, 0xFFFF_FFFEu, 0x10u),
            ],
            result.Commits.Take(4).Select(c => (c.Pc, (int)c.Register, c.Value, c.NextPc)));

        Assert.All(result.Commits.Take(4), c =>
        {
            Assert.Equal(StopReason.None, c.Stop);
            Assert.False(c.WritesMemory);
            Assert.False(c.Trapped);
            Assert.True(c.WritesRegister);
        });
        Assert.Equal(Op.Add, result.Commits[2].Instruction.Op);

        // The fifth record is the end of the program: there was nothing left to fetch.
        Assert.Equal(5, result.Commits.Count);
        Assert.Equal((0x10u, StopReason.EndOfProgram), (result.Last.Pc, result.Last.Stop));
        Assert.Equal(4ul, result.Hart.InstructionsRetired);
    }

    [Fact]
    public void RegisterZeroIsNeverWritten()
    {
        var result = RunToEnd("addi x0, x0, 5\nadd  x0, sp, sp\nlui  x0, 0x12345\njal  x0, next\nnext: addi a0, x0, 1");

        Assert.Equal(0u, result["zero"]);
        Assert.Equal(1u, result["a0"]);
        Assert.All(result.Commits.Take(4), c => Assert.Equal((0, 0u), (c.Register, c.Value)));
    }

    [Fact]
    public void UpperImmediates()
    {
        var result = RunToEnd("nop\nlui a0, 0xFFFFF\nauipc a1, 0x1\nauipc a2, 0xFFFFF\nlui a3, 0");

        Assert.Equal(0xFFFF_F000u, result["a0"]);
        Assert.Equal(0x0000_1008u, result["a1"]);     // its own address, 8, plus 0x1000
        Assert.Equal(0xFFFF_F00Cu, result["a2"]);     // wraps around
        Assert.Equal(0u, result["a3"]);
    }

    [Fact]
    public void ImmediateArithmeticSignExtendsItsTwelveBits()
    {
        var result = RunToEnd("""
            li    t0, 100
            addi  a0, t0, -1
            slti  a1, t0, 101
            slti  a2, t0, -1
            sltiu a3, t0, -1          # -1 is 0xffffffff when compared unsigned
            xori  a4, t0, -1          # not
            ori   a5, t0, 0x700
            andi  a6, t0, 0x0F
            slli  a7, t0, 4
            srli  s2, a4, 28
            srai  s3, a4, 28
            """);

        Assert.Equal(99u, result["a0"]);
        Assert.Equal(1u, result["a1"]);
        Assert.Equal(0u, result["a2"]);
        Assert.Equal(1u, result["a3"]);
        Assert.Equal(0xFFFF_FF9Bu, result["a4"]);
        Assert.Equal(0x764u, result["a5"]);
        Assert.Equal(4u, result["a6"]);
        Assert.Equal(1600u, result["a7"]);
        Assert.Equal(0xFu, result["s2"]);
        Assert.Equal(0xFFFF_FFFFu, result["s3"]);
    }

    [Fact]
    public void LoadsExtendAndStoresTruncate()
    {
        var result = RunToEnd("""
            .data
            cell:  .word 0
            bytes: .byte 0x80, 0x7F
            half:  .half 0x8001
            .text
                la   s0, cell
                li   t0, 0x12345678
                sw   t0, 0(s0)
                sh   t0, 8(s0)           # only 0x5678 is stored
                sb   t0, 12(s0)          # only 0x78
                lw   a0, 0(s0)
                lb   a1, 4(s0)           # 0x80: negative as a signed byte
                lbu  a2, 4(s0)
                lb   a3, 5(s0)
                lh   a4, 6(s0)           # 0x8001: negative as a signed half
                lhu  a5, 6(s0)
                lw   a6, 8(s0)
                lbu  a7, 12(s0)
                lh   s1, 1(s0)           # misaligned: bytes 1 and 2 of 0x12345678
            """);

        Assert.Equal(0x1234_5678u, result["a0"]);
        Assert.Equal(0xFFFF_FF80u, result["a1"]);
        Assert.Equal(0x80u, result["a2"]);
        Assert.Equal(0x7Fu, result["a3"]);
        Assert.Equal(0xFFFF_8001u, result["a4"]);
        Assert.Equal(0x8001u, result["a5"]);
        Assert.Equal(0x5678u, result["a6"]);
        Assert.Equal(0x78u, result["a7"]);
        Assert.Equal(0x3456u, result["s1"]);

        var stores = result.Commits.Where(c => c.WritesMemory).ToList();
        Assert.Equal(
            [
                (0x1000_0000u, 4, 0x1234_5678u),
                (0x1000_0008u, 2, 0x5678u),
                (0x1000_000Cu, 1, 0x78u),
            ],
            stores.Select(c => (c.StoreAddress, (int)c.StoreBytes, c.StoreValue)));
        Assert.All(stores, c => Assert.False(c.WritesRegister));
    }

    [Fact]
    public void ALoadFromTheStackWorksBeforeAnythingWasStoredThere()
    {
        var result = RunToEnd("addi sp, sp, -16\nsw ra, 12(sp)\nlw a0, 12(sp)\nlw a1, 0(sp)\naddi sp, sp, 16");

        Assert.Equal(20u, result["a0"]);
        Assert.Equal(0u, result["a1"]);
        Assert.Equal(0x7FFF_FFF0u, result["sp"]);
    }

    [Fact]
    public void BranchesGoWhereTheirConditionSays()
    {
        var result = RunToEnd("""
                li   t0, 3
                li   a0, 0
            loop:
                addi a0, a0, 10
                addi t0, t0, -1
                bnez t0, loop            # taken twice, then falls through
                beq  a0, t0, never
                li   a1, 1
                bltu t0, a0, unsigned    # 0 < 30
                li   a1, 99
            unsigned:
                li   t1, -1
                blt  t1, t0, signed      # -1 < 0 as signed
                li   a1, 98
            signed:
                bgeu t1, t0, done        # 0xffffffff >= 0 as unsigned
            never:
                li   a1, 97
            done:
            """);

        Assert.Equal(30u, result["a0"]);
        Assert.Equal(1u, result["a1"]);

        var branches = result.Commits.Where(c => c.Instruction.Op == Op.Bne).ToList();
        Assert.Equal([0x8u, 0x8u, 0x14u], branches.Select(c => c.NextPc));
        Assert.All(branches, c => Assert.Equal(0x10u, c.Pc));
    }

    [Fact]
    public void JumpsLinkToTheInstructionAfterThem()
    {
        var result = RunToEnd("""
                jal  ra, first           # 0x00
                li   a5, 1               # 0x04: where first returns to
                j    over                # 0x08
            first:
                mv   a0, ra              # 0x0c
                jalr t0, 0(ra)           # 0x10: return, linking to 0x14
            over:
                la   t1, odd             # 0x14
                jalr a1, 1(t1)           # 0x1c: the lowest bit of the target is cleared
                li   a6, 99              # 0x20: skipped
            odd:
                li   a7, 7               # 0x24
            """);

        Assert.Equal(4u, result["a0"]);
        Assert.Equal(0x14u, result["t0"]);
        Assert.Equal(1u, result["a5"]);
        Assert.Equal(0x20u, result["a1"]);
        Assert.Equal(0u, result["a6"]);
        Assert.Equal(7u, result["a7"]);

        var jump = result.Commits.Single(c => c.Pc == 0x1C);
        Assert.Equal((11, 0x20u, 0x24u), (jump.Register, jump.Value, jump.NextPc));
    }

    [Fact]
    public void MultiplyAndDivideRunThroughTheSameAlu()
    {
        var result = RunToEnd("""
            li     t0, -7
            li     t1, 2
            mul    a0, t0, t1
            mulh   a1, t0, t1
            mulhu  a2, t0, t1
            mulhsu a3, t0, t1
            div    a4, t0, t1
            divu   a5, t0, t1
            rem    a6, t0, t1
            remu   a7, t0, t1
            div    s2, t0, zero
            rem    s3, t0, zero
            """);

        Assert.Equal(unchecked((uint)-14), result["a0"]);
        Assert.Equal(0xFFFF_FFFFu, result["a1"]);
        Assert.Equal(1u, result["a2"]);
        Assert.Equal(0xFFFF_FFFFu, result["a3"]);
        Assert.Equal(unchecked((uint)-3), result["a4"]);
        Assert.Equal(0x7FFF_FFFCu, result["a5"]);
        Assert.Equal(unchecked((uint)-1), result["a6"]);
        Assert.Equal(1u, result["a7"]);
        Assert.Equal(0xFFFF_FFFFu, result["s2"]);
        Assert.Equal(unchecked((uint)-7), result["s3"]);
    }

    [Fact]
    public void FenceAndWfiDoNothing()
    {
        var result = RunToEnd("li a0, 1\nfence\nfence.i\nwfi\nli a1, 2");

        Assert.Equal((1u, 2u), (result["a0"], result["a1"]));
        Assert.Equal(5ul, result.Hart.InstructionsRetired);
        Assert.All(result.Commits.Skip(1).Take(3), c => Assert.False(c.WritesRegister || c.WritesMemory));
    }

    [Fact]
    public void TheCountersCountInstructionsAndSteps()
    {
        var result = RunToEnd("li t0, 100\nloop: addi t0, t0, -1\nbnez t0, loop");

        // 1 + 100 * 2 instructions; the step that found the end of the program is a cycle too.
        Assert.Equal(201ul, result.Hart.InstructionsRetired);
        Assert.Equal(202ul, result.Hart.Cycle);
        Assert.Equal(202, result.Commits.Count);
    }

    [Fact]
    public void AFinishedMachineStaysFinished()
    {
        var result = RunToEnd("li a0, 1");
        var machine = result.Machine;

        Assert.True(machine.IsFinished);
        Assert.Equal(StopReason.EndOfProgram, machine.Stopped);

        var again = machine.Step();
        Assert.Equal(result.Last, again);
        Assert.Equal(again, machine.Run());
        Assert.Equal(1ul, machine.Hart.InstructionsRetired);
        Assert.Equal(2ul, machine.Hart.Cycle);
    }

    [Fact]
    public void RunStopsWhenItsBudgetIsSpent()
    {
        var machine = new ReferenceMachine(Asm.AssemblerTesting.Assemble("loop: addi a0, a0, 1\nj loop"));

        var commit = machine.Run(maxInstructions: 1000);

        Assert.Equal(StopReason.None, commit.Stop);
        Assert.Equal(500u, machine.Hart.X[10]);
        Assert.False(machine.IsFinished);

        machine.Run(maxInstructions: 10);
        Assert.Equal(505u, machine.Hart.X[10]);
    }

    [Fact]
    public void RecordsWithTheSameContentsAreEqual()
    {
        // The lockstep check is an equality of records, so equality must mean every field.
        var a = RunToEnd("li a0, 5\nsw a0, 0(gp)").Commits;
        var b = RunToEnd("li a0, 5\nsw a0, 0(gp)").Commits;
        var c = RunToEnd("li a0, 6\nsw a0, 0(gp)").Commits;

        Assert.Equal(a, b);
        Assert.NotEqual(a[0], c[0]);
        Assert.NotEqual(a[1], c[1]);     // the same instruction, a different value stored
        Assert.Equal(a[2], c[2]);
    }
}
