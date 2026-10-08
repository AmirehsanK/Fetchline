using Fetchline.Core.Isa;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Isa;

public class EncoderTests
{
    [Theory]
    [InlineData(Op.Add, 3, 1, 2, 0, 0x002081B3u)]
    [InlineData(Op.Addi, 1, 0, 0, -1, 0xFFF00093u)]
    [InlineData(Op.Lw, 4, 2, 0, 12, 0x00C12203u)]
    [InlineData(Op.Sw, 0, 2, 1, -4, 0xFE112E23u)]
    [InlineData(Op.Beq, 0, 1, 2, -4, 0xFE208EE3u)]
    [InlineData(Op.Lui, 1, 0, 0, 0x12345000, 0x123450B7u)]
    [InlineData(Op.Jal, 1, 0, 0, -4, 0xFFDFF0EFu)]
    [InlineData(Op.Srai, 10, 10, 0, 3, 0x40355513u)]
    [InlineData(Op.Csrrs, 10, 0, 0, 0xF14, 0xF1402573u)]
    [InlineData(Op.Csrrwi, 0, 5, 0, 0x300, 0x3002D073u)]
    [InlineData(Op.Ecall, 0, 0, 0, 0, 0x00000073u)]
    [InlineData(Op.Mret, 0, 0, 0, 0, 0x30200073u)]
    public void KnownFieldsEncodeToTheirWord(Op op, int rd, int rs1, int rs2, int imm, uint expected)
    {
        Assert.True(Encoder.TryEncode(op, rd, rs1, rs2, imm, out var word, out var error));
        Assert.Equal(EncodeError.None, error);
        Assert.Equal(expected, word);
    }

    [Theory]
    [InlineData(Op.Illegal, 0, 0, 0, 0, EncodeError.NotAnInstruction)]
    [InlineData(Op.Add, 32, 0, 0, 0, EncodeError.RegisterOutOfRange)]
    [InlineData(Op.Add, 0, -1, 0, 0, EncodeError.RegisterOutOfRange)]
    [InlineData(Op.Add, 0, 0, 99, 0, EncodeError.RegisterOutOfRange)]
    [InlineData(Op.Addi, 1, 1, 0, 2048, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Addi, 1, 1, 0, -2049, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Sw, 0, 1, 1, 2048, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Slli, 1, 1, 0, 32, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Slli, 1, 1, 0, -1, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Beq, 0, 1, 1, 4096, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Beq, 0, 1, 1, -4098, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Beq, 0, 1, 1, 3, EncodeError.OffsetIsOdd)]
    [InlineData(Op.Jal, 1, 0, 0, 1048576, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Jal, 1, 0, 0, 5, EncodeError.OffsetIsOdd)]
    [InlineData(Op.Lui, 1, 0, 0, 0x12345678, EncodeError.UpperImmediateHasLowBits)]
    [InlineData(Op.Csrrw, 1, 1, 0, 4096, EncodeError.ImmediateOutOfRange)]
    [InlineData(Op.Csrrw, 1, 1, 0, -1, EncodeError.ImmediateOutOfRange)]
    public void FieldsThatDoNotFitAreRefusedWithAReason(Op op, int rd, int rs1, int rs2, int imm, EncodeError expected)
    {
        Assert.False(Encoder.TryEncode(op, rd, rs1, rs2, imm, out _, out var error));
        Assert.Equal(expected, error);
        Assert.Throws<ArgumentException>(() => Encoder.Encode(op, rd, rs1, rs2, imm));
    }

    [Fact]
    public void EveryInstructionSurvivesEncodeThenDecodeOnRandomFields()
    {
        var random = new SeededRandom(0xF37C_0001);
        foreach (var def in InstructionSet.All)
        {
            for (var i = 0; i < 2000; i++)
            {
                var fields = RandomFields(def, random);
                var word = Encoder.Encode(fields.Op, fields.Rd, fields.Rs1, fields.Rs2, fields.Imm);
                var decoded = Decoder.Decode(word);

                if (decoded != fields with { Raw = word })
                {
                    Assert.Fail($"seed {random.Seed:X}: {def.Mnemonic} {fields} encoded to {word:X8}, decoded to {decoded}");
                }
            }
        }
    }

    [Fact]
    public void EveryLegalWordSurvivesDecodeThenEncode()
    {
        // The other direction, over arbitrary words: whatever decodes must encode back to the
        // very same bits, so no field of any format is dropped or invented by the decoder.
        var random = new SeededRandom(0xF37C_0002);
        var legal = 0;
        for (var i = 0; i < 2_000_000; i++)
        {
            // Most random words are not instructions; forcing the low bits and an opcode from
            // the table gets a useful share of legal ones without leaving the rest untested.
            var word = random.NextUInt32();
            if (random.Chance(75))
            {
                word = (word & ~0x7Fu) | (random.Pick(InstructionSet.All).Match & 0x7F);
            }

            var decoded = Decoder.Decode(word);
            if (!decoded.IsLegal)
            {
                continue;
            }

            legal++;
            var encoded = Encoder.Encode(decoded);
            if (encoded != word)
            {
                Assert.Fail($"seed {random.Seed:X}: {word:X8} decoded to {decoded} and encoded back to {encoded:X8}");
            }
        }

        Assert.True(legal > 500_000, $"only {legal} legal words were tried");
    }

    /// <summary>Valid random fields for one instruction, in the convention of <see cref="Instruction"/>.</summary>
    internal static Instruction RandomFields(InstructionDef def, SeededRandom random)
    {
        byte Register() => (byte)random.Next(0, 31);

        // Half the immediates sit at or near the ends of the range, where encodings go wrong.
        int Immediate(int min, int max, int step = 1)
        {
            var value = random.Next(0, 9) switch
            {
                0 => min,
                1 => max,
                2 => 0,
                3 => min + step,
                4 => max - step,
                _ => random.Next(min, max),
            };
            return value - (((value % step) + step) % step);
        }

        var (min, max) = Encoder.ImmediateRange(def.Format);
        return def.Format switch
        {
            Format.R => new Instruction(def.Op, Register(), Register(), Register(), 0, 0),
            Format.I or Format.Shift => new Instruction(def.Op, Register(), Register(), 0, Immediate(min, max), 0),
            Format.S => new Instruction(def.Op, 0, Register(), Register(), Immediate(min, max), 0),
            Format.B => new Instruction(def.Op, 0, Register(), Register(), Immediate(min, max, 2), 0),
            Format.U => new Instruction(def.Op, Register(), 0, 0, (int)(random.NextUInt32() & 0xFFFFF000), 0),
            Format.J => new Instruction(def.Op, Register(), 0, 0, Immediate(min, max, 2), 0),
            Format.Csr or Format.CsrImm or Format.Fence =>
                new Instruction(def.Op, Register(), Register(), 0, Immediate(min, max), 0),
            _ => new Instruction(def.Op, 0, 0, 0, 0, 0),
        };
    }
}
