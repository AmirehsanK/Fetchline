using Fetchline.Core.Machine;
using Fetchline.Core.Trace;
using static Fetchline.Tests.Machine.MachineTesting;

namespace Fetchline.Tests.Machine;

/// <summary>What surrounds a program run by the playground or by <c>fetchline run</c>.</summary>
public class HostEnvironmentTests
{
    [Fact]
    public void PrintIntWritesASignedDecimal()
    {
        var result = Run("li a0, -42\nli a7, 1\necall\nli a0, 0x7FFFFFFF\necall\nli a0, 0x80000000\necall");

        Assert.Equal("-422147483647-2147483648", result.Output);
        Assert.Equal(StopReason.EndOfProgram, result.Last.Stop);
    }

    [Fact]
    public void PrintCharWritesTheLowByte()
    {
        var result = Run("li a7, 11\nli a0, 'H'\necall\nli a0, 0x169\necall\nli a0, '\\n'\necall");

        Assert.Equal("Hi\n", result.Output);
    }

    [Fact]
    public void PrintStringStopsAtTheTerminator()
    {
        var result = Run("""
            .data
            first:  .asciz "Hello, "
            second: .asciz "world!\n"
            empty:  .asciz ""
            .text
                li a7, 4
                la a0, first
                ecall
                la a0, empty
                ecall
                la a0, second
                ecall
            """);

        Assert.Equal("Hello, world!\n", result.Output);
    }

    [Fact]
    public void TextOutsideAsciiComesOutWholeEvenOneByteAtATime()
    {
        var result = Run("""
            .data
            greeting: .asciz "سلام دنیا é 😀"
            .text
                la   s0, greeting
                li   a7, 11
            next:
                lbu  a0, 0(s0)
                beqz a0, done
                ecall                    # one byte of a multi-byte character per call
                addi s0, s0, 1
                j    next
            done:
            """);

        Assert.Equal("سلام دنیا é 😀", result.Output);
    }

    [Fact]
    public void WriteSendsBytesAndReturnsHowMany()
    {
        var result = Run("""
            .data
            msg: .ascii "abcdef"
            .text
                li a7, 64
                li a0, 1
                la a1, msg
                li a2, 4
                ecall
                mv s0, a0                # 4
                li a0, 2                 # standard error goes to the same console
                li a2, 2
                la a1, msg + 4
                ecall
                li a0, 7                 # there is no file 7
                ecall
                mv s1, a0
                li a0, 1
                li a2, 0                 # nothing to write is not an error
                ecall
                mv s2, a0
            """);

        Assert.Equal("abcdef", result.Output);
        Assert.Equal(4u, result["s0"]);
        Assert.Equal(unchecked((uint)-9), result["s1"]);
        Assert.Equal(0u, result["s2"]);

        // The result arrives in a0, so the system call itself is recorded as writing a register.
        var call = result.Commits.First(c => c.Instruction.Op == Fetchline.Core.Isa.Op.Ecall);
        Assert.Equal((10, 4u), (call.Register, call.Value));
    }

    [Theory]
    [InlineData("li a7, 10\necall\nli a0, 99", 0)]
    [InlineData("li a0, 3\nli a7, 93\necall\nli a0, 99", 3)]
    [InlineData("li a0, -1\nli a7, 93\necall", -1)]
    public void ExitEndsTheProgramWithItsCode(string source, int code)
    {
        var result = Run(source);

        Assert.Equal((StopReason.Exit, code), (result.Last.Stop, result.Last.ExitCode));
        Assert.NotEqual(99u, result["a0"]);
        Assert.True(result.Machine.IsFinished);
    }

    [Fact]
    public void EbreakPausesAndTheProgramCanGoOn()
    {
        var result = Run("li a0, 1\nebreak\nli a0, 2\nebreak\nli a0, 3");
        var machine = result.Machine;

        Assert.Equal((StopReason.Breakpoint, 4u, 8u), (result.Last.Stop, result.Last.Pc, result.Last.NextPc));
        Assert.Equal(1u, result["a0"]);
        Assert.False(machine.IsFinished);

        Assert.Equal(StopReason.Breakpoint, machine.Run().Stop);
        Assert.Equal(2u, machine.Hart.X[10]);

        Assert.Equal(StopReason.EndOfProgram, machine.Run().Stop);
        Assert.Equal(3u, machine.Hart.X[10]);
        Assert.True(machine.IsFinished);
    }

    [Fact]
    public void RunningOffTheEndIsACleanStop()
    {
        var result = Run("li a0, 1");

        Assert.Equal((StopReason.EndOfProgram, 4u, 4u), (result.Last.Stop, result.Last.Pc, result.Last.NextPc));
        Assert.Null(result.Last.Message);
    }

    [Fact]
    public void ReturningFromTheEntryFunctionIsACleanStopToo()
    {
        var result = Run("""
            main:
                addi sp, sp, -16
                sw   ra, 12(sp)
                call helper
                lw   ra, 12(sp)
                addi sp, sp, 16
                ret
            helper:
                li   a0, 7
                ret
            """);

        Assert.Equal(StopReason.EndOfProgram, result.Last.Stop);
        Assert.Equal(7u, result["a0"]);
        Assert.Equal(0x7FFF_FFF0u, result["sp"]);
    }

    [Theory]
    [InlineData("sw a0, 0(zero)", "a store to 0x00000000, which is in 'text' and cannot be written, at pc 0x00000000")]
    [InlineData("nop\nlw a0, -4(zero)", "a load from 0xfffffffc, which is outside the memory this program has, at pc 0x00000004")]
    [InlineData("li t0, 0x20000000\nsb a0, 0(t0)", "a store to 0x20000000, which is outside the memory this program has, at pc 0x00000004")]
    [InlineData("lui t0, 0x10000\njr t0", "a jump to 0x10000000, which is in 'data and heap' and is not code, at pc 0x10000000")]
    [InlineData("li t0, 0x5000\njr t0", "a jump to 0x00005000, which is outside the program, at pc 0x00005000")]
    [InlineData("li t0, 6\njr t0", "a jump to 0x00000006, which is not a multiple of 4, at pc 0x00000004")]
    [InlineData("nop\n.word 0", "an illegal instruction (0x00000000), at pc 0x00000004")]
    [InlineData(".word 0xFFFFFFFF", "an illegal instruction (0xffffffff), at pc 0x00000000")]
    [InlineData("li a7, 999\necall", "an ecall with a7 = 999, which is not a system call this machine has, at pc 0x00000004")]
    [InlineData("li a7, 4\nli a0, 0x30000000\necall", "a load from 0x30000000, which is outside the memory this program has, while printing a string, at pc 0x00000008")]
    [InlineData("li a7, 64\nli a0, 1\nli a1, 0x1FFFFFF0\nli a2, 32\necall", "a load from 0x20000000, which is outside the memory this program has, while writing output, at pc 0x00000014")]
    [InlineData("li a7, 64\nli a0, 1\nli a2, -1\necall", "a write of 4294967295 bytes, which is more than this machine prints at once, at pc 0x0000000c")]
    [InlineData(".rodata\nk: .word 1\n.text\nla t0, k\nsw zero, 0(t0)", "a store to 0x10000000, which is in 'rodata' and cannot be written, at pc 0x00000008")]
    public void WhatAProgramCannotDoStopsItWithASentence(string source, string message)
    {
        var result = Run(source);

        Assert.Equal(StopReason.Fault, result.Last.Stop);
        Assert.Equal(message, result.Last.Message);
        Assert.True(result.Machine.IsFinished);
        Assert.False(result.Last.WritesRegister || result.Last.WritesMemory);
    }

    [Fact]
    public void AFaultingInstructionChangesNothing()
    {
        // The store is refused, the link register of the bad jump is not written, and the
        // program counter stays on the instruction that failed.
        var store = Run("li a0, 5\nsw a0, 0(zero)\nli a0, 9");
        Assert.Equal(5u, store["a0"]);
        Assert.Equal(4u, store.Hart.Pc);
        Assert.Equal(0x0050_0513u, store.Hart.Memory.ReadU32(0));
        Assert.Equal(1ul, store.Hart.InstructionsRetired);

        var jump = Run("li t0, 6\njalr a1, 0(t0)");
        Assert.Equal(0u, jump["a1"]);
        Assert.Equal(4u, jump.Hart.Pc);
    }

    [Fact]
    public void RecursionWithoutAnEndRunsOutOfStack()
    {
        var result = Run("down:\n  addi sp, sp, -16\n  sw ra, 12(sp)\n  call down", maxInstructions: 1_000_000);

        Assert.Equal(StopReason.Fault, result.Last.Stop);
        Assert.Equal(
            "a store to 0x7feffffc, which is outside the memory this program has, at pc 0x00000004",
            result.Last.Message);
    }

    [Fact]
    public void AProgramCanUseItsHeapAndItsStack()
    {
        var result = RunToEnd("""
            .data
            v: .word 1
            .text
                la   t0, v
                li   t1, 0x12345
                sw   t1, 1024(t0)        # past the program's own data: the heap
                lw   a0, 1024(t0)
                sw   t1, -256(sp)
                lw   a1, -256(sp)
            """);

        Assert.Equal((0x12345u, 0x12345u), (result["a0"], result["a1"]));
    }

    [Fact]
    public void OutputGoesToTheWriterTheMachineWasGiven()
    {
        var first = new StringWriter();
        var second = new StringWriter();
        var program = Asm.AssemblerTesting.Assemble("li a0, 7\nli a7, 1\necall");

        new ReferenceMachine(program, first).Run();
        new ReferenceMachine(program, second).Run();
        var silent = new ReferenceMachine(program);
        silent.Run();

        Assert.Equal("7", first.ToString());
        Assert.Equal("7", second.ToString());
        Assert.Equal("7", silent.Hart.Output.ToString());
    }
}
