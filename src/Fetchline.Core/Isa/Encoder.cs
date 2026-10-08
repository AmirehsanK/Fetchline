namespace Fetchline.Core.Isa;

/// <summary>Why a set of fields cannot be encoded.</summary>
public enum EncodeError : byte
{
    None,

    /// <summary><see cref="Op.Illegal"/> has no encoding.</summary>
    NotAnInstruction,

    /// <summary>A register number outside 0..31.</summary>
    RegisterOutOfRange,

    /// <summary>The immediate does not fit the format's field.</summary>
    ImmediateOutOfRange,

    /// <summary>A branch or jump offset is odd; the encoding has no bit for it.</summary>
    OffsetIsOdd,

    /// <summary>An upper immediate with something in its low twelve bits.</summary>
    UpperImmediateHasLowBits,
}

/// <summary>Turns fields back into a 32-bit word: the inverse of <see cref="Decoder"/>.</summary>
public static class Encoder
{
    /// <summary>
    /// The smallest and largest value of <see cref="Instruction.Imm"/> a format can hold, for
    /// messages. Branch and jump offsets must also be even.
    /// </summary>
    public static (int Min, int Max) ImmediateRange(Format format) => format switch
    {
        Format.I or Format.S => (-2048, 2047),
        Format.Shift => (0, 31),
        Format.B => (-4096, 4094),
        Format.J => (-1048576, 1048574),
        Format.U => (int.MinValue, int.MaxValue),
        Format.Csr or Format.CsrImm or Format.Fence => (0, 4095),
        _ => (0, 0),
    };

    /// <summary>Encodes an instruction from its fields, in the convention of <see cref="Instruction"/>.</summary>
    public static bool TryEncode(Op op, int rd, int rs1, int rs2, int imm, out uint word, out EncodeError error)
    {
        word = 0;
        if (op == Op.Illegal)
        {
            error = EncodeError.NotAnInstruction;
            return false;
        }

        var def = InstructionSet.Get(op);
        if ((uint)rd > 31 || (uint)rs1 > 31 || (uint)rs2 > 31)
        {
            error = EncodeError.RegisterOutOfRange;
            return false;
        }

        var (min, max) = ImmediateRange(def.Format);
        if (def.Format is not (Format.R or Format.Fixed) && (imm < min || imm > max))
        {
            error = EncodeError.ImmediateOutOfRange;
            return false;
        }

        if (def.Format is Format.B or Format.J && (imm & 1) != 0)
        {
            error = EncodeError.OffsetIsOdd;
            return false;
        }

        if (def.Format == Format.U && (imm & 0xFFF) != 0)
        {
            error = EncodeError.UpperImmediateHasLowBits;
            return false;
        }

        word = def.Match | def.Format switch
        {
            Format.R => Bits.PackRd(rd) | Bits.PackRs1(rs1) | Bits.PackRs2(rs2),
            Format.I or Format.Csr or Format.CsrImm or Format.Fence =>
                Bits.PackRd(rd) | Bits.PackRs1(rs1) | Bits.PackI(imm),
            Format.Shift => Bits.PackRd(rd) | Bits.PackRs1(rs1) | Bits.PackRs2(imm),
            Format.S => Bits.PackRs1(rs1) | Bits.PackRs2(rs2) | Bits.PackS(imm),
            Format.B => Bits.PackRs1(rs1) | Bits.PackRs2(rs2) | Bits.PackB(imm),
            Format.U => Bits.PackRd(rd) | Bits.PackU(imm),
            Format.J => Bits.PackRd(rd) | Bits.PackJ(imm),
            _ => 0,
        };
        error = EncodeError.None;
        return true;
    }

    /// <summary>Encodes fields that are known to be valid; throws when they are not.</summary>
    public static uint Encode(Op op, int rd = 0, int rs1 = 0, int rs2 = 0, int imm = 0) =>
        TryEncode(op, rd, rs1, rs2, imm, out var word, out var error)
            ? word
            : throw new ArgumentException($"Cannot encode {op} (rd {rd}, rs1 {rs1}, rs2 {rs2}, imm {imm}): {error}.");

    /// <summary>Encodes a decoded instruction; for a legal one this gives back its own word.</summary>
    public static uint Encode(in Instruction instruction) =>
        Encode(instruction.Op, instruction.Rd, instruction.Rs1, instruction.Rs2, instruction.Imm);
}
