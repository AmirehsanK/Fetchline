namespace Fetchline.Core.Isa;

/// <summary>What the ALU computes.</summary>
public enum AluOp : byte
{
    Add, Sub, Sll, Slt, Sltu, Xor, Srl, Sra, Or, And,
    Mul, Mulh, Mulhsu, Mulhu, Div, Divu, Rem, Remu,
}

/// <summary>The ALU's first operand.</summary>
public enum SrcA : byte
{
    Rs1,
    Pc,
    Zero,
}

/// <summary>The ALU's second operand.</summary>
public enum SrcB : byte
{
    Rs2,
    Imm,
}

/// <summary>What is written back to <c>rd</c>.</summary>
public enum WbSrc : byte
{
    None,
    Alu,
    Mem,
    PcPlus4,
    Csr,
}

/// <summary>Whether the instruction touches data memory.</summary>
public enum MemOp : byte
{
    None,
    Load,
    Store,
}

/// <summary>How the instruction can change the program counter.</summary>
public enum BranchKind : byte
{
    None,
    Beq, Bne, Blt, Bge, Bltu, Bgeu,
    Jal,
    Jalr,
}

/// <summary>
/// An instruction that changes machine state beyond <c>rd</c>, memory and the program counter.
/// These take effect at the commit point and then restart the instructions behind them.
/// </summary>
public enum SystemOp : byte
{
    None,
    Ecall,
    Ebreak,
    Mret,
    FenceI,
    CsrWrite,
    CsrSet,
    CsrClear,
}

/// <summary>What kind of work an instruction is, for counters and explanations.</summary>
public enum InstructionClass : byte
{
    Arithmetic,
    Load,
    Store,
    Branch,
    Jump,
    MulDiv,
    System,
    Nop,
}

/// <summary>
/// The control signals of one instruction: everything the datapath needs to know about it besides
/// its register numbers and its immediate. Both machines are driven by these and by nothing else,
/// which is why they cannot disagree about what an instruction means.
/// </summary>
/// <param name="UsesRs1">The value of <c>rs1</c> is really read. False for <c>lui</c>, <c>jal</c>, a CSR-immediate…</param>
/// <param name="UsesRs2">The value of <c>rs2</c> is really read. False for every I-type.</param>
/// <param name="WritesRd">A value is written to <c>rd</c> (unless <c>rd</c> is <c>x0</c>).</param>
/// <param name="MemBytes">Width of the access: 1, 2 or 4. Zero when there is none.</param>
/// <param name="MemSigned">A load narrower than a word is sign-extended.</param>
/// <param name="CsrImmediate">A CSR instruction takes its operand from the <c>rs1</c> field itself.</param>
public readonly record struct Control(
    bool UsesRs1,
    bool UsesRs2,
    bool WritesRd,
    SrcA SrcA,
    SrcB SrcB,
    AluOp Alu,
    WbSrc Wb,
    MemOp Mem,
    byte MemBytes,
    bool MemSigned,
    BranchKind Branch,
    SystemOp System,
    bool CsrImmediate,
    InstructionClass Class)
{
    /// <summary>The instruction can redirect the program counter.</summary>
    public bool IsControlFlow => Branch != BranchKind.None;

    /// <summary>A conditional branch, as opposed to a jump.</summary>
    public bool IsConditionalBranch => Branch is >= BranchKind.Beq and <= BranchKind.Bgeu;

    /// <summary>A CSR read, write, set or clear.</summary>
    public bool IsCsr => System is SystemOp.CsrWrite or SystemOp.CsrSet or SystemOp.CsrClear;

    /// <summary>A multiply or divide, which the pipeline can be told to take several cycles over.</summary>
    public bool IsMulDiv => Alu >= AluOp.Mul;
}
