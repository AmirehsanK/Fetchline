using System.Globalization;

namespace Fetchline.Core.Isa;

/// <summary>How the disassembler writes an instruction.</summary>
public sealed record DisassemblyOptions
{
    /// <summary>Alias forms (<c>li</c>, <c>mv</c>, <c>ret</c>…), ABI register names.</summary>
    public static DisassemblyOptions Default { get; } = new();

    /// <summary>The real instruction behind every alias, as <c>objdump -M no-aliases</c> shows it.</summary>
    public static DisassemblyOptions Canonical { get; } = new() { Aliases = false };

    public RegisterStyle Registers { get; init; } = RegisterStyle.Abi;

    /// <summary>Whether to show <c>addi a0, zero, 5</c> as <c>li a0, 5</c>, and so on.</summary>
    public bool Aliases { get; init; } = true;

    /// <summary>
    /// The label at an address, when there is one. A branch or jump target is then written as
    /// that label; otherwise it is written as an absolute address in hex.
    /// </summary>
    public Func<uint, string?>? SymbolAt { get; init; }
}

/// <summary>An instruction as text, with the mnemonic apart so a listing can align its columns.</summary>
public readonly record struct DisassembledLine(string Mnemonic, string Operands)
{
    public override string ToString() => Operands.Length == 0 ? Mnemonic : Mnemonic + " " + Operands;
}

/// <summary>
/// Writes an instruction the way the assembler reads it, so that assembling the text at the same
/// address gives the same word back. A word that is not an instruction becomes a <c>.word</c>.
/// </summary>
public static class Disassembler
{
    /// <summary>Disassembles the word at <paramref name="pc"/>.</summary>
    public static DisassembledLine Disassemble(uint word, uint pc, DisassemblyOptions? options = null) =>
        Disassemble(Decoder.Decode(word), pc, options);

    /// <summary>Disassembles a decoded instruction that sits at <paramref name="pc"/>.</summary>
    public static DisassembledLine Disassemble(in Instruction instruction, uint pc, DisassemblyOptions? options = null)
    {
        options ??= DisassemblyOptions.Default;
        if (!instruction.IsLegal || !IsWritable(instruction))
        {
            return new DisassembledLine(".word", Hex(instruction.Raw, "x8"));
        }

        if (options.Aliases && TryAlias(instruction, pc, options, out var alias))
        {
            return alias;
        }

        var def = InstructionSet.Get(instruction.Op);
        string Reg(int index) => Registers.Name(index, options.Registers);
        var (rd, rs1, rs2, imm) = (instruction.Rd, instruction.Rs1, instruction.Rs2, instruction.Imm);

        var operands = def.Syntax switch
        {
            Syntax.RdRs1Rs2 => $"{Reg(rd)}, {Reg(rs1)}, {Reg(rs2)}",
            Syntax.RdRs1Imm or Syntax.RdRs1Shamt => $"{Reg(rd)}, {Reg(rs1)}, {Dec(imm)}",
            Syntax.RdMem => $"{Reg(rd)}, {Dec(imm)}({Reg(rs1)})",
            Syntax.Rs2Mem => $"{Reg(rs2)}, {Dec(imm)}({Reg(rs1)})",
            Syntax.Rs1Rs2Target => $"{Reg(rs1)}, {Reg(rs2)}, {Target(pc, imm, options)}",
            Syntax.RdTarget => $"{Reg(rd)}, {Target(pc, imm, options)}",
            Syntax.RdUpper => $"{Reg(rd)}, {Hex((uint)imm >> 12, "x")}",
            Syntax.RdCsrRs1 => $"{Reg(rd)}, {Csr.Format(imm)}, {Reg(rs1)}",
            Syntax.RdCsrZimm => $"{Reg(rd)}, {Csr.Format(imm)}, {Dec(rs1)}",
            Syntax.Fence => $"{FenceSet(imm >> 4)}, {FenceSet(imm)}",
            _ => string.Empty,
        };
        return new DisassembledLine(def.Mnemonic, operands);
    }

    /// <summary>The instruction at <paramref name="pc"/> as one string.</summary>
    public static string Format(uint word, uint pc, DisassemblyOptions? options = null) =>
        Disassemble(word, pc, options).ToString();

    // fence and fence.i have reserved fields that assembly has no way to write. A word that uses
    // them is legal to execute but can only be shown as data, or the text would not round-trip.
    private static bool IsWritable(in Instruction instruction) => instruction.Op switch
    {
        Op.Fence => instruction.Rd == 0 && instruction.Rs1 == 0
            && (instruction.Imm >> 8) == 0 && (instruction.Imm & 0xF0) != 0 && (instruction.Imm & 0x0F) != 0,
        Op.FenceI => instruction.Rd == 0 && instruction.Rs1 == 0 && instruction.Imm == 0,
        _ => true,
    };

    private static bool TryAlias(in Instruction i, uint pc, DisassemblyOptions options, out DisassembledLine line)
    {
        string Reg(int index) => Registers.Name(index, options.Registers);
        var (rd, rs1, rs2, imm) = (i.Rd, i.Rs1, i.Rs2, i.Imm);

        (string Mnemonic, string Operands)? alias = i.Op switch
        {
            Op.Addi when rd == 0 && rs1 == 0 && imm == 0 => ("nop", string.Empty),
            Op.Addi when rs1 == 0 => ("li", $"{Reg(rd)}, {Dec(imm)}"),
            Op.Addi when imm == 0 => ("mv", $"{Reg(rd)}, {Reg(rs1)}"),
            Op.Xori when imm == -1 => ("not", $"{Reg(rd)}, {Reg(rs1)}"),
            Op.Sub when rs1 == 0 => ("neg", $"{Reg(rd)}, {Reg(rs2)}"),
            Op.Sltiu when imm == 1 => ("seqz", $"{Reg(rd)}, {Reg(rs1)}"),
            Op.Sltu when rs1 == 0 => ("snez", $"{Reg(rd)}, {Reg(rs2)}"),
            Op.Slt when rs2 == 0 => ("sltz", $"{Reg(rd)}, {Reg(rs1)}"),
            Op.Slt when rs1 == 0 => ("sgtz", $"{Reg(rd)}, {Reg(rs2)}"),

            Op.Beq when rs2 == 0 => ("beqz", $"{Reg(rs1)}, {Target(pc, imm, options)}"),
            Op.Bne when rs2 == 0 => ("bnez", $"{Reg(rs1)}, {Target(pc, imm, options)}"),
            Op.Bge when rs2 == 0 => ("bgez", $"{Reg(rs1)}, {Target(pc, imm, options)}"),
            Op.Bge when rs1 == 0 => ("blez", $"{Reg(rs2)}, {Target(pc, imm, options)}"),
            Op.Blt when rs2 == 0 => ("bltz", $"{Reg(rs1)}, {Target(pc, imm, options)}"),
            Op.Blt when rs1 == 0 => ("bgtz", $"{Reg(rs2)}, {Target(pc, imm, options)}"),

            Op.Jal when rd == 0 => ("j", Target(pc, imm, options)),
            Op.Jal when rd == 1 => ("jal", Target(pc, imm, options)),
            Op.Jalr when rd == 0 && rs1 == 1 && imm == 0 => ("ret", string.Empty),
            Op.Jalr when rd == 0 && imm == 0 => ("jr", Reg(rs1)),
            Op.Jalr when rd == 1 && imm == 0 => ("jalr", Reg(rs1)),

            Op.Csrrs when rs1 == 0 => ("csrr", $"{Reg(rd)}, {Csr.Format(imm)}"),
            Op.Csrrw when rd == 0 => ("csrw", $"{Csr.Format(imm)}, {Reg(rs1)}"),
            Op.Csrrs when rd == 0 => ("csrs", $"{Csr.Format(imm)}, {Reg(rs1)}"),
            Op.Csrrc when rd == 0 => ("csrc", $"{Csr.Format(imm)}, {Reg(rs1)}"),
            Op.Csrrwi when rd == 0 => ("csrwi", $"{Csr.Format(imm)}, {Dec(rs1)}"),
            Op.Csrrsi when rd == 0 => ("csrsi", $"{Csr.Format(imm)}, {Dec(rs1)}"),
            Op.Csrrci when rd == 0 => ("csrci", $"{Csr.Format(imm)}, {Dec(rs1)}"),

            Op.Fence when (imm & 0xFF) == 0xFF => ("fence", string.Empty),
            _ => null,
        };

        line = alias is { } found ? new DisassembledLine(found.Mnemonic, found.Operands) : default;
        return alias is not null;
    }

    private static string Target(uint pc, int offset, DisassemblyOptions options)
    {
        var target = pc + (uint)offset;
        return options.SymbolAt?.Invoke(target) ?? Hex(target, "x");
    }

    // The four bits of a fence set, most significant first: device input, device output, reads, writes.
    private static string FenceSet(int bits)
    {
        Span<char> letters = stackalloc char[4];
        var length = 0;
        for (var bit = 3; bit >= 0; bit--)
        {
            if ((bits & (1 << bit)) != 0)
            {
                letters[length++] = "wroi"[bit];
            }
        }

        return new string(letters[..length]);
    }

    private static string Dec(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(uint value, string format) => "0x" + value.ToString(format, CultureInfo.InvariantCulture);
}
