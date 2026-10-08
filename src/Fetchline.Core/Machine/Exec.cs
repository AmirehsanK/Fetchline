using Fetchline.Core.Isa;

namespace Fetchline.Core.Machine;

/// <summary>
/// What instructions compute. The reference machine and the pipeline both call these and
/// nothing else for arithmetic, comparisons, targets and load extension, so the two cannot
/// disagree about what an instruction means: the official tests prove these functions on one
/// machine, and that proof carries to the other.
/// </summary>
public static class Exec
{
    /// <summary>The ALU. Shifts use the low five bits of <paramref name="b"/>, as the hardware does.</summary>
    public static uint Alu(AluOp op, uint a, uint b) => op switch
    {
        AluOp.Add => a + b,
        AluOp.Sub => a - b,
        AluOp.Sll => a << (int)(b & 31),
        AluOp.Slt => (int)a < (int)b ? 1u : 0u,
        AluOp.Sltu => a < b ? 1u : 0u,
        AluOp.Xor => a ^ b,
        AluOp.Srl => a >> (int)(b & 31),
        AluOp.Sra => (uint)((int)a >> (int)(b & 31)),
        AluOp.Or => a | b,
        AluOp.And => a & b,

        AluOp.Mul => a * b,
        AluOp.Mulh => (uint)(((long)(int)a * (int)b) >> 32),
        AluOp.Mulhsu => (uint)(((long)(int)a * b) >> 32),
        AluOp.Mulhu => (uint)(((ulong)a * b) >> 32),

        // Division never traps in RISC-V. Dividing by zero gives all ones and leaves the dividend
        // as the remainder; the one signed overflow, -2^31 / -1, gives the dividend and no remainder.
        AluOp.Div => b == 0 ? uint.MaxValue : IsOverflow(a, b) ? a : (uint)((int)a / (int)b),
        AluOp.Divu => b == 0 ? uint.MaxValue : a / b,
        AluOp.Rem => b == 0 ? a : IsOverflow(a, b) ? 0u : (uint)((int)a % (int)b),
        AluOp.Remu => b == 0 ? a : a % b,
        _ => 0,
    };

    /// <summary>Whether a branch is taken. A jump always is; an instruction that is neither never is.</summary>
    public static bool Taken(BranchKind kind, uint rs1, uint rs2) => kind switch
    {
        BranchKind.Beq => rs1 == rs2,
        BranchKind.Bne => rs1 != rs2,
        BranchKind.Blt => (int)rs1 < (int)rs2,
        BranchKind.Bge => (int)rs1 >= (int)rs2,
        BranchKind.Bltu => rs1 < rs2,
        BranchKind.Bgeu => rs1 >= rs2,
        BranchKind.Jal or BranchKind.Jalr => true,
        _ => false,
    };

    /// <summary>
    /// Where control goes when a branch or jump is taken. <c>jalr</c> jumps to the ALU's sum of
    /// <c>rs1</c> and the immediate with its lowest bit cleared; everything else is relative to
    /// the instruction's own address.
    /// </summary>
    public static uint Target(BranchKind kind, uint pc, int imm, uint aluResult) =>
        kind == BranchKind.Jalr ? aluResult & ~1u : pc + (uint)imm;

    /// <summary>The ALU's first operand.</summary>
    public static uint OperandA(SrcA source, uint rs1, uint pc) => source switch
    {
        SrcA.Rs1 => rs1,
        SrcA.Pc => pc,
        _ => 0,
    };

    /// <summary>The ALU's second operand.</summary>
    public static uint OperandB(SrcB source, uint rs2, int imm) => source == SrcB.Imm ? (uint)imm : rs2;

    /// <summary>Widens what a load read to 32 bits: a byte or a half is sign- or zero-extended.</summary>
    public static uint Extend(uint raw, int bytes, bool signed) => bytes switch
    {
        1 => signed ? (uint)(sbyte)raw : raw & 0xFF,
        2 => signed ? (uint)(short)raw : raw & 0xFFFF,
        _ => raw,
    };

    /// <summary>What a CSR instruction writes back, from the register's old value and its operand.</summary>
    public static uint CsrUpdate(SystemOp op, uint old, uint operand) => op switch
    {
        SystemOp.CsrWrite => operand,
        SystemOp.CsrSet => old | operand,
        SystemOp.CsrClear => old & ~operand,
        _ => old,
    };

    private static bool IsOverflow(uint a, uint b) => a == 0x8000_0000 && b == 0xFFFF_FFFF;
}
