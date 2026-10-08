using System.Globalization;
using System.Text;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Trace;

namespace Fetchline.Core.Machine;

/// <summary>What surrounds a program: who answers its <c>ecall</c>, and what memory it has.</summary>
public enum ExecutionEnvironment : byte
{
    /// <summary>
    /// The playground and <c>fetchline run</c>. <c>ecall</c> is a system call, <c>ebreak</c>
    /// pauses, memory is mapped, and a mistake stops the program with a sentence.
    /// </summary>
    Host,

    /// <summary>
    /// Bare metal, as the official tests expect. <c>ecall</c> and <c>ebreak</c> trap to
    /// <c>mtvec</c>, memory is flat, and a write to <c>tohost</c> ends the run.
    /// </summary>
    Bare,
}

/// <summary>
/// A hart is RISC-V's word for one hardware thread: the registers, the program counter, the
/// memory and everything around them that an instruction can affect. Both machines own one.
///
/// What differs between the machines is how an instruction's inputs are gathered: the reference
/// machine reads them all at once, the pipeline over several cycles with forwarding. What an
/// instruction then does to the world is decided in one place, <see cref="Complete"/>, which is
/// the pipeline's commit point and the second half of the reference machine's step.
/// </summary>
public sealed class Hart
{
    // The system calls of the host environment, chosen by a7.
    private const uint PrintInt = 1;
    private const uint PrintString = 4;
    private const uint ExitZero = 10;
    private const uint PrintChar = 11;
    private const uint Write = 64;
    private const uint ExitWithCode = 93;

    // A string or a write longer than this is a runaway pointer, not output.
    private const int MaxOutputBytes = 1024 * 1024;

    private const int A0 = 10;
    private const int A1 = 11;
    private const int A2 = 12;
    private const int A7 = 17;

    private readonly System.Text.Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly uint _textStart;
    private readonly uint _textSpan;
    private readonly bool _hasText;
    private readonly bool _hasTohost;
    private readonly uint _tohost;

    /// <param name="environment">
    /// What surrounds the program. When not given, a program with a <c>tohost</c> symbol is taken
    /// to be a bare-metal test and anything else to be a host program.
    /// </param>
    public Hart(Program program, TextWriter? output = null, ExecutionEnvironment? environment = null)
    {
        Program = program;
        Output = output ?? new StringWriter();
        Memory = new Memory();
        foreach (var segment in program.Segments)
        {
            Memory.WriteBytes(segment.Address, segment.Data.Span);
        }

        _hasTohost = program.TryGetSymbol("tohost", out var tohost);
        _tohost = tohost?.Value ?? 0;
        Environment = environment ?? (_hasTohost ? ExecutionEnvironment.Bare : ExecutionEnvironment.Host);
        Pc = program.Entry;

        var text = program.Text;
        TextEnd = text is null ? program.Entry : text.Address + text.Size;
        if (Environment == ExecutionEnvironment.Bare)
        {
            // Bare metal: flat memory, every register zero, and nothing is checked.
            return;
        }

        Map = MemoryMap.ForHost(program);
        _textStart = text?.Address ?? 0;
        _textSpan = text is { Size: >= 4 } ? text.Size - 4 : 0;
        _hasText = text is { Size: >= 4 };

        X[2] = MemoryMap.StackTop;
        X[3] = MemoryMap.DataBase;

        // A program whose entry function ends with "ret" returns to here: the address just past
        // the code, where running off the end stops it cleanly.
        X[1] = TextEnd;
    }

    public Program Program { get; }

    public ExecutionEnvironment Environment { get; }

    /// <summary>The integer registers. <c>x0</c> is never written.</summary>
    public uint[] X { get; } = new uint[Registers.Count];

    public uint Pc { get; set; }

    public Memory Memory { get; }

    /// <summary>The control and status registers.</summary>
    public CsrFile Csrs { get; } = new();

    /// <summary>What a host program may touch. A bare program has no map: its memory is flat.</summary>
    public MemoryMap? Map { get; }

    /// <summary>Where the program's console output goes.</summary>
    public TextWriter Output { get; }

    /// <summary>The address just past the last instruction. Reaching it ends a host program.</summary>
    public uint TextEnd { get; }

    /// <summary>How many instructions have completed.</summary>
    public ulong InstructionsRetired
    {
        get => Csrs.InstructionsRetired;
        set => Csrs.InstructionsRetired = value;
    }

    /// <summary>How many clock cycles have passed. The machine that owns the hart advances it.</summary>
    public ulong Cycle
    {
        get => Csrs.Cycle;
        set => Csrs.Cycle = value;
    }

    /// <summary>
    /// Reads the instruction word at an address. When there is none, <paramref name="stop"/> is
    /// the record of why: the clean end of the program, or a jump to somewhere that is not code.
    /// </summary>
    public bool TryFetch(uint pc, out uint word, out Commit stop)
    {
        // Almost every fetch is from the one text segment; the map is only asked about the rest.
        if (Map is null || (_hasText && pc - _textStart <= _textSpan) || Map.Allows(pc, 4, Access.Execute))
        {
            word = Memory.ReadU32(pc);
            stop = default;
            return true;
        }

        word = 0;
        stop = pc == TextEnd
            ? new Commit { Pc = pc, NextPc = pc, Stop = StopReason.EndOfProgram }
            : Fault(pc, default, Map.Explain(pc, 4, Access.Execute));
        return false;
    }

    /// <summary>
    /// The commit point: everything an instruction does besides writing its register. Memory is
    /// read or written, a system call runs, and the record of it all is returned. The caller
    /// writes <see cref="Commit.Register"/> (at once in the reference machine, a stage later in
    /// the pipeline) and follows <see cref="Commit.NextPc"/>.
    /// </summary>
    /// <param name="rs1">The value of <c>rs1</c> the instruction saw.</param>
    /// <param name="rs2">The value of <c>rs2</c> the instruction saw: what a store writes.</param>
    /// <param name="alu">The ALU's result: an arithmetic result, or the address of a load or store.</param>
    /// <param name="taken">Whether the branch or jump is taken.</param>
    /// <param name="target">Where a taken branch or jump goes.</param>
    public Commit Complete(uint pc, in Instruction instruction, uint rs1, uint rs2, uint alu, bool taken, uint target)
    {
        if (!instruction.IsLegal)
        {
            return Raise(pc, instruction, TrapCause.IllegalInstruction, instruction.Raw);
        }

        var control = instruction.Control;
        var next = pc + 4;
        if (taken)
        {
            // Without compressed instructions every instruction starts at a multiple of four.
            // The jump itself is the one at fault, so it must not write its link register.
            if ((target & 3) != 0)
            {
                return Raise(pc, instruction, TrapCause.InstructionAddressMisaligned, target);
            }

            next = target;
        }

        var value = control.Wb == WbSrc.PcPlus4 ? pc + 4 : alu;
        byte storeBytes = 0;
        uint storeAddress = 0, storeValue = 0;
        var stop = StopReason.None;
        var exitCode = 0;

        // A misaligned load or store is carried out, not trapped: the official ma_data test
        // requires it, and memory here has no alignment of its own.
        switch (control.Mem)
        {
            case MemOp.Load:
                if (Map is not null && !Map.Allows(alu, control.MemBytes, Access.Read))
                {
                    return Fault(pc, instruction, Map.Explain(alu, control.MemBytes, Access.Read));
                }

                value = Exec.Extend(Memory.Read(alu, control.MemBytes), control.MemBytes, control.MemSigned);
                break;

            case MemOp.Store:
                if (Map is not null && !Map.Allows(alu, control.MemBytes, Access.Write))
                {
                    return Fault(pc, instruction, Map.Explain(alu, control.MemBytes, Access.Write));
                }

                storeBytes = control.MemBytes;
                storeAddress = alu;
                storeValue = storeBytes == 4 ? rs2 : rs2 & ((1u << (8 * storeBytes)) - 1);
                Memory.Write(alu, storeBytes, rs2);

                // A test program ends by writing its verdict to the word named tohost.
                if (_hasTohost && Environment == ExecutionEnvironment.Bare && alu - _tohost < 4
                    && Memory.ReadU32(_tohost) is var verdict and not 0)
                {
                    stop = StopReason.Tohost;
                    exitCode = (int)verdict;
                }

                break;
        }

        switch (control.System)
        {
            case SystemOp.None or SystemOp.FenceI:
                break;

            case SystemOp.Ecall:
                return Environment == ExecutionEnvironment.Host
                    ? SystemCall(pc, instruction)
                    : Raise(pc, instruction, TrapCause.EcallFromMachine, 0);

            case SystemOp.Ebreak when Environment == ExecutionEnvironment.Host:
                // A pause, not an end: the program counter moves on so that it can be resumed.
                InstructionsRetired++;
                return new Commit { Pc = pc, Instruction = instruction, NextPc = next, Stop = StopReason.Breakpoint };

            case SystemOp.Ebreak:
                return Raise(pc, instruction, TrapCause.Breakpoint, pc);

            case SystemOp.Mret:
                next = Csrs.ReturnFromTrap();
                break;

            default:
            {
                // A CSR instruction reads the register into rd and writes a new value made from the
                // old one and its operand. The set and clear forms write nothing when the operand
                // field is zero, which is how a read-only register can be read at all.
                var number = instruction.Imm;
                var operand = control.CsrImmediate ? instruction.Rs1 : rs1;
                var writes = control.System == SystemOp.CsrWrite || instruction.Rs1 != 0;

                if (!Csrs.TryRead(number, out var old)
                    || (writes && !Csrs.TryWrite(number, Exec.CsrUpdate(control.System, old, operand))))
                {
                    return Raise(pc, instruction, TrapCause.IllegalInstruction, instruction.Raw);
                }

                value = old;
                break;
            }
        }

        // An instruction that wrote minstret set the count the next instruction reads, so it
        // does not also count itself.
        if (Csrs.WroteInstret)
        {
            Csrs.WroteInstret = false;
        }
        else
        {
            InstructionsRetired++;
        }

        var register = control.WritesRd ? instruction.Rd : (byte)0;
        return new Commit
        {
            Pc = pc,
            Instruction = instruction,
            Register = register,
            Value = register == 0 ? 0 : value,
            StoreBytes = storeBytes,
            StoreAddress = storeAddress,
            StoreValue = storeValue,
            NextPc = next,
            Stop = stop,
            ExitCode = exitCode,
        };
    }

    private Commit SystemCall(uint pc, in Instruction instruction)
    {
        // System calls belong to the host environment, which always has a map.
        var map = Map!;
        var commit = new Commit { Pc = pc, Instruction = instruction, NextPc = pc + 4 };
        var (number, argument) = (X[A7], X[A0]);

        switch (number)
        {
            case PrintInt:
                Output.Write(((int)argument).ToString(CultureInfo.InvariantCulture));
                break;

            case PrintChar:
                Print([(byte)argument]);
                break;

            case PrintString:
            {
                var bytes = new List<byte>();
                for (var address = argument; ; address++)
                {
                    if (!map.Allows(address, 1, Access.Read))
                    {
                        return Fault(pc, instruction, map.Explain(address, 1, Access.Read) + ", while printing a string");
                    }

                    var value = Memory.ReadByte(address);
                    if (value == 0)
                    {
                        break;
                    }

                    bytes.Add(value);
                    if (bytes.Count > MaxOutputBytes)
                    {
                        return Fault(pc, instruction, $"a string of more than {MaxOutputBytes} bytes with no end");
                    }
                }

                Print(bytes.ToArray());
                break;
            }

            case Write:
            {
                // write(fd, buffer, count), as on Linux. Only the two output streams exist.
                var (buffer, count) = (X[A1], X[A2]);
                if (argument is not (1 or 2))
                {
                    commit = commit with { Register = A0, Value = unchecked((uint)-9) };   // EBADF
                    break;
                }

                if (count > MaxOutputBytes)
                {
                    return Fault(pc, instruction, $"a write of {count} bytes, which is more than this machine prints at once");
                }

                for (uint i = 0; i < count; i++)
                {
                    if (!map.Allows(buffer + i, 1, Access.Read))
                    {
                        return Fault(pc, instruction, map.Explain(buffer + i, 1, Access.Read) + ", while writing output");
                    }
                }

                var bytes = new byte[count];
                Memory.ReadBytes(buffer, bytes);
                Print(bytes);
                commit = commit with { Register = A0, Value = count };
                break;
            }

            case ExitZero:
                commit = commit with { Stop = StopReason.Exit };
                break;

            case ExitWithCode:
                commit = commit with { Stop = StopReason.Exit, ExitCode = (int)argument };
                break;

            default:
                return Fault(
                    pc, instruction, $"an ecall with a7 = {number}, which is not a system call this machine has");
        }

        InstructionsRetired++;
        return commit;
    }

    // Output is bytes to the program and text to whoever reads it. The decoder keeps the state
    // between calls, so a multi-byte character printed one byte at a time still comes out whole.
    private void Print(ReadOnlySpan<byte> bytes)
    {
        Span<char> text = stackalloc char[256];
        while (!bytes.IsEmpty)
        {
            _utf8.Convert(bytes, text, flush: false, out var used, out var produced, out _);
            Output.Write(text[..produced]);
            bytes = bytes[used..];
        }
    }

    /// <summary>
    /// An exception. On bare metal it is delivered: the cause is recorded in the CSRs and control
    /// goes to the trap vector, which is what a test program expects. A host program has no
    /// handler to go to, so there it is a fault, and the program stops with a sentence.
    /// </summary>
    private Commit Raise(uint pc, in Instruction instruction, uint cause, uint value)
    {
        if (Environment == ExecutionEnvironment.Bare)
        {
            return new Commit
            {
                Pc = pc,
                Instruction = instruction,
                Trapped = true,
                Cause = cause,
                TrapValue = value,
                NextPc = Csrs.EnterTrap(pc, cause, value),
            };
        }

        return Fault(pc, instruction, cause switch
        {
            TrapCause.IllegalInstruction when instruction is { IsLegal: true, Control.IsCsr: true } =>
                $"an access to CSR {Csr.Format(instruction.Imm)}, which this machine does not have or cannot write",
            TrapCause.IllegalInstruction => $"an illegal instruction (0x{instruction.Raw:x8})",
            TrapCause.InstructionAddressMisaligned => $"a jump to 0x{value:x8}, which is not a multiple of 4",
            _ => "an exception (" + TrapCause.Name(cause) + ")",
        });
    }

    private static Commit Fault(uint pc, in Instruction instruction, string what) => new()
    {
        Pc = pc,
        Instruction = instruction,
        NextPc = pc,
        Stop = StopReason.Fault,
        Message = string.Create(CultureInfo.InvariantCulture, $"{what}, at pc 0x{pc:x8}"),
    };
}
