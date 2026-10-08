namespace Fetchline.Core.Isa;

/// <summary>
/// Every instruction the core implements, plus <see cref="Illegal"/> for a word that is none of
/// them. The order is the order of the instruction table.
/// </summary>
public enum Op : byte
{
    Illegal = 0,

    // RV32I
    Lui, Auipc, Jal, Jalr,
    Beq, Bne, Blt, Bge, Bltu, Bgeu,
    Lb, Lh, Lw, Lbu, Lhu,
    Sb, Sh, Sw,
    Addi, Slti, Sltiu, Xori, Ori, Andi, Slli, Srli, Srai,
    Add, Sub, Sll, Slt, Sltu, Xor, Srl, Sra, Or, And,
    Fence, Ecall, Ebreak,

    // M
    Mul, Mulh, Mulhsu, Mulhu, Div, Divu, Rem, Remu,

    // Zicsr
    Csrrw, Csrrs, Csrrc, Csrrwi, Csrrsi, Csrrci,

    // Zifencei
    FenceI,

    // Privileged
    Mret, Wfi,
}

/// <summary>Which bits of the word are fields, and which fields they are.</summary>
public enum Format : byte
{
    /// <summary><c>funct7 rs2 rs1 funct3 rd opcode</c></summary>
    R,

    /// <summary><c>imm[11:0] rs1 funct3 rd opcode</c></summary>
    I,

    /// <summary>An I-type whose immediate is a five-bit shift amount under a fixed <c>funct7</c>.</summary>
    Shift,

    /// <summary><c>imm[11:5] rs2 rs1 funct3 imm[4:0] opcode</c></summary>
    S,

    /// <summary>An S-type layout holding a branch offset.</summary>
    B,

    /// <summary><c>imm[31:12] rd opcode</c></summary>
    U,

    /// <summary>A U-type layout holding a jump offset.</summary>
    J,

    /// <summary><c>csr rs1 funct3 rd opcode</c></summary>
    Csr,

    /// <summary><c>csr zimm funct3 rd opcode</c>: the <c>rs1</c> field is a five-bit constant.</summary>
    CsrImm,

    /// <summary><c>fm pred succ rs1 funct3 rd opcode</c>. Only <c>funct3</c> and the opcode are fixed.</summary>
    Fence,

    /// <summary>Every bit is fixed: <c>ecall</c>, <c>ebreak</c>, <c>mret</c>, <c>wfi</c>.</summary>
    Fixed,
}

/// <summary>How an instruction's operands are written in assembly.</summary>
public enum Syntax : byte
{
    /// <summary><c>ecall</c></summary>
    None,

    /// <summary><c>add rd, rs1, rs2</c></summary>
    RdRs1Rs2,

    /// <summary><c>addi rd, rs1, imm</c></summary>
    RdRs1Imm,

    /// <summary><c>slli rd, rs1, shamt</c></summary>
    RdRs1Shamt,

    /// <summary><c>lw rd, imm(rs1)</c>, and <c>jalr rd, imm(rs1)</c></summary>
    RdMem,

    /// <summary><c>sw rs2, imm(rs1)</c></summary>
    Rs2Mem,

    /// <summary><c>beq rs1, rs2, target</c></summary>
    Rs1Rs2Target,

    /// <summary><c>jal rd, target</c></summary>
    RdTarget,

    /// <summary><c>lui rd, imm20</c></summary>
    RdUpper,

    /// <summary><c>csrrw rd, csr, rs1</c></summary>
    RdCsrRs1,

    /// <summary><c>csrrwi rd, csr, zimm</c></summary>
    RdCsrZimm,

    /// <summary><c>fence pred, succ</c></summary>
    Fence,
}

/// <summary>The extension an instruction belongs to.</summary>
public enum Extension : byte
{
    I,
    M,
    Zicsr,
    Zifencei,
    Privileged,
}
