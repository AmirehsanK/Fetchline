using System.Collections.Frozen;

namespace Fetchline.Core.Isa;

/// <summary>One row of the instruction table.</summary>
/// <param name="Match">The fixed bits of the encoding.</param>
/// <param name="Mask">Which bits are fixed. A word is this instruction when <c>(word &amp; Mask) == Match</c>.</param>
public sealed record InstructionDef(
    Op Op,
    string Mnemonic,
    Format Format,
    uint Match,
    uint Mask,
    Syntax Syntax,
    Extension Extension,
    Control Control);

/// <summary>
/// The instruction table: RV32I, M, Zicsr and Zifencei, plus <c>mret</c> and <c>wfi</c>. This is
/// the only place in the code that knows an opcode. The decoder, the encoder, the assembler and
/// the disassembler all read it, and a test holds every match and mask in it to the official
/// <c>riscv-opcodes</c> files.
/// </summary>
public static class InstructionSet
{
    private const uint OpcodeLoad = 0x03;
    private const uint OpcodeMiscMem = 0x0F;
    private const uint OpcodeImm = 0x13;
    private const uint OpcodeAuipc = 0x17;
    private const uint OpcodeStore = 0x23;
    private const uint OpcodeReg = 0x33;
    private const uint OpcodeLui = 0x37;
    private const uint OpcodeBranch = 0x63;
    private const uint OpcodeJalr = 0x67;
    private const uint OpcodeJal = 0x6F;
    private const uint OpcodeSystem = 0x73;

    private const uint MaskOpcode = 0x0000_007F;
    private const uint MaskFunct3 = 0x0000_707F;
    private const uint MaskFunct7 = 0xFE00_707F;
    private const uint MaskAll = 0xFFFF_FFFF;

    private static readonly InstructionDef[] Rows = Build();
    private static readonly InstructionDef?[] ByOp = IndexByOp(Rows);
    private static readonly FrozenDictionary<string, InstructionDef> ByMnemonic =
        Rows.ToFrozenDictionary(row => row.Mnemonic, StringComparer.Ordinal);

    /// <summary>Every instruction, in the order of <see cref="Op"/>.</summary>
    public static IReadOnlyList<InstructionDef> All => Rows;

    /// <summary>The row for an instruction. <see cref="Op.Illegal"/> has none.</summary>
    public static InstructionDef Get(Op op) =>
        ByOp[(int)op] ?? throw new ArgumentOutOfRangeException(nameof(op), op, "Not an instruction.");

    /// <summary>The control signals of an instruction; all clear for <see cref="Op.Illegal"/>.</summary>
    public static Control ControlOf(Op op) => ByOp[(int)op]?.Control ?? default;

    /// <summary>Looks an instruction up by its mnemonic, which is lower case.</summary>
    public static bool TryGet(string mnemonic, out InstructionDef def) => ByMnemonic.TryGetValue(mnemonic, out def!);

    private static InstructionDef?[] IndexByOp(InstructionDef[] rows)
    {
        var index = new InstructionDef?[Enum.GetValues<Op>().Length];
        foreach (var row in rows)
        {
            index[(int)row.Op] = row;
        }

        return index;
    }

    private static InstructionDef[] Build() =>
    [
        // ---- RV32I: upper immediates and jumps ----
        Upper(Op.Lui, "lui", OpcodeLui, SrcA.Zero),
        Upper(Op.Auipc, "auipc", OpcodeAuipc, SrcA.Pc),
        new(Op.Jal, "jal", Format.J, OpcodeJal, MaskOpcode, Syntax.RdTarget, Extension.I,
            new Control(false, false, true, SrcA.Pc, SrcB.Imm, AluOp.Add, WbSrc.PcPlus4,
                MemOp.None, 0, false, BranchKind.Jal, SystemOp.None, false, InstructionClass.Jump)),
        new(Op.Jalr, "jalr", Format.I, OpcodeJalr, MaskFunct3, Syntax.RdMem, Extension.I,
            new Control(true, false, true, SrcA.Rs1, SrcB.Imm, AluOp.Add, WbSrc.PcPlus4,
                MemOp.None, 0, false, BranchKind.Jalr, SystemOp.None, false, InstructionClass.Jump)),

        // ---- RV32I: conditional branches ----
        Branch(Op.Beq, "beq", 0, BranchKind.Beq),
        Branch(Op.Bne, "bne", 1, BranchKind.Bne),
        Branch(Op.Blt, "blt", 4, BranchKind.Blt),
        Branch(Op.Bge, "bge", 5, BranchKind.Bge),
        Branch(Op.Bltu, "bltu", 6, BranchKind.Bltu),
        Branch(Op.Bgeu, "bgeu", 7, BranchKind.Bgeu),

        // ---- RV32I: loads and stores ----
        Load(Op.Lb, "lb", 0, 1, signed: true),
        Load(Op.Lh, "lh", 1, 2, signed: true),
        Load(Op.Lw, "lw", 2, 4, signed: true),
        Load(Op.Lbu, "lbu", 4, 1, signed: false),
        Load(Op.Lhu, "lhu", 5, 2, signed: false),
        Store(Op.Sb, "sb", 0, 1),
        Store(Op.Sh, "sh", 1, 2),
        Store(Op.Sw, "sw", 2, 4),

        // ---- RV32I: register-immediate ----
        Immediate(Op.Addi, "addi", 0, AluOp.Add),
        Immediate(Op.Slti, "slti", 2, AluOp.Slt),
        Immediate(Op.Sltiu, "sltiu", 3, AluOp.Sltu),
        Immediate(Op.Xori, "xori", 4, AluOp.Xor),
        Immediate(Op.Ori, "ori", 6, AluOp.Or),
        Immediate(Op.Andi, "andi", 7, AluOp.And),
        // RV32 has five-bit shift amounts. With bit 25 set these are RV64 encodings, which RV32
        // reserves, so funct7 is part of the match and such a word decodes as illegal.
        ShiftImmediate(Op.Slli, "slli", 0x00, 1, AluOp.Sll),
        ShiftImmediate(Op.Srli, "srli", 0x00, 5, AluOp.Srl),
        ShiftImmediate(Op.Srai, "srai", 0x20, 5, AluOp.Sra),

        // ---- RV32I: register-register ----
        Register(Op.Add, "add", 0x00, 0, AluOp.Add, Extension.I),
        Register(Op.Sub, "sub", 0x20, 0, AluOp.Sub, Extension.I),
        Register(Op.Sll, "sll", 0x00, 1, AluOp.Sll, Extension.I),
        Register(Op.Slt, "slt", 0x00, 2, AluOp.Slt, Extension.I),
        Register(Op.Sltu, "sltu", 0x00, 3, AluOp.Sltu, Extension.I),
        Register(Op.Xor, "xor", 0x00, 4, AluOp.Xor, Extension.I),
        Register(Op.Srl, "srl", 0x00, 5, AluOp.Srl, Extension.I),
        Register(Op.Sra, "sra", 0x20, 5, AluOp.Sra, Extension.I),
        Register(Op.Or, "or", 0x00, 6, AluOp.Or, Extension.I),
        Register(Op.And, "and", 0x00, 7, AluOp.And, Extension.I),

        // ---- RV32I: memory ordering and environment ----
        // With one hart and no cache, fence orders nothing: it is a no-op that reads no register.
        new(Op.Fence, "fence", Format.Fence, OpcodeMiscMem, MaskFunct3, Syntax.Fence, Extension.I,
            Plain(InstructionClass.Nop)),
        Fixed(Op.Ecall, "ecall", 0x0000_0073, Extension.I, SystemOp.Ecall),
        Fixed(Op.Ebreak, "ebreak", 0x0010_0073, Extension.I, SystemOp.Ebreak),

        // ---- M ----
        Register(Op.Mul, "mul", 0x01, 0, AluOp.Mul, Extension.M),
        Register(Op.Mulh, "mulh", 0x01, 1, AluOp.Mulh, Extension.M),
        Register(Op.Mulhsu, "mulhsu", 0x01, 2, AluOp.Mulhsu, Extension.M),
        Register(Op.Mulhu, "mulhu", 0x01, 3, AluOp.Mulhu, Extension.M),
        Register(Op.Div, "div", 0x01, 4, AluOp.Div, Extension.M),
        Register(Op.Divu, "divu", 0x01, 5, AluOp.Divu, Extension.M),
        Register(Op.Rem, "rem", 0x01, 6, AluOp.Rem, Extension.M),
        Register(Op.Remu, "remu", 0x01, 7, AluOp.Remu, Extension.M),

        // ---- Zicsr ----
        CsrRegister(Op.Csrrw, "csrrw", 1, SystemOp.CsrWrite),
        CsrRegister(Op.Csrrs, "csrrs", 2, SystemOp.CsrSet),
        CsrRegister(Op.Csrrc, "csrrc", 3, SystemOp.CsrClear),
        CsrImmediate(Op.Csrrwi, "csrrwi", 5, SystemOp.CsrWrite),
        CsrImmediate(Op.Csrrsi, "csrrsi", 6, SystemOp.CsrSet),
        CsrImmediate(Op.Csrrci, "csrrci", 7, SystemOp.CsrClear),

        // ---- Zifencei ----
        // An I-type whose rd, rs1 and immediate are reserved and ignored. It is a system
        // instruction here because the pipeline must refetch whatever it had already fetched.
        new(Op.FenceI, "fence.i", Format.I, OpcodeMiscMem | (1u << 12), MaskFunct3, Syntax.None,
            Extension.Zifencei, Plain(InstructionClass.System) with { System = SystemOp.FenceI }),

        // ---- Privileged ----
        Fixed(Op.Mret, "mret", 0x3020_0073, Extension.Privileged, SystemOp.Mret),
        // There are no interrupts to wait for, so wfi returns at once: a no-op.
        new(Op.Wfi, "wfi", Format.Fixed, 0x1050_0073, MaskAll, Syntax.None, Extension.Privileged,
            Plain(InstructionClass.Nop)),
    ];

    private static Control Plain(InstructionClass kind) =>
        new(false, false, false, SrcA.Zero, SrcB.Imm, AluOp.Add, WbSrc.None,
            MemOp.None, 0, false, BranchKind.None, SystemOp.None, false, kind);

    private static InstructionDef Upper(Op op, string mnemonic, uint opcode, SrcA srcA) =>
        new(op, mnemonic, Format.U, opcode, MaskOpcode, Syntax.RdUpper, Extension.I,
            new Control(false, false, true, srcA, SrcB.Imm, AluOp.Add, WbSrc.Alu,
                MemOp.None, 0, false, BranchKind.None, SystemOp.None, false, InstructionClass.Arithmetic));

    private static InstructionDef Branch(Op op, string mnemonic, uint funct3, BranchKind kind) =>
        new(op, mnemonic, Format.B, OpcodeBranch | (funct3 << 12), MaskFunct3, Syntax.Rs1Rs2Target, Extension.I,
            new Control(true, true, false, SrcA.Rs1, SrcB.Rs2, AluOp.Sub, WbSrc.None,
                MemOp.None, 0, false, kind, SystemOp.None, false, InstructionClass.Branch));

    private static InstructionDef Load(Op op, string mnemonic, uint funct3, byte bytes, bool signed) =>
        new(op, mnemonic, Format.I, OpcodeLoad | (funct3 << 12), MaskFunct3, Syntax.RdMem, Extension.I,
            new Control(true, false, true, SrcA.Rs1, SrcB.Imm, AluOp.Add, WbSrc.Mem,
                MemOp.Load, bytes, signed, BranchKind.None, SystemOp.None, false, InstructionClass.Load));

    private static InstructionDef Store(Op op, string mnemonic, uint funct3, byte bytes) =>
        new(op, mnemonic, Format.S, OpcodeStore | (funct3 << 12), MaskFunct3, Syntax.Rs2Mem, Extension.I,
            new Control(true, true, false, SrcA.Rs1, SrcB.Imm, AluOp.Add, WbSrc.None,
                MemOp.Store, bytes, false, BranchKind.None, SystemOp.None, false, InstructionClass.Store));

    private static InstructionDef Immediate(Op op, string mnemonic, uint funct3, AluOp alu) =>
        new(op, mnemonic, Format.I, OpcodeImm | (funct3 << 12), MaskFunct3, Syntax.RdRs1Imm, Extension.I,
            new Control(true, false, true, SrcA.Rs1, SrcB.Imm, alu, WbSrc.Alu,
                MemOp.None, 0, false, BranchKind.None, SystemOp.None, false, InstructionClass.Arithmetic));

    private static InstructionDef ShiftImmediate(Op op, string mnemonic, uint funct7, uint funct3, AluOp alu) =>
        new(op, mnemonic, Format.Shift, OpcodeImm | (funct3 << 12) | (funct7 << 25), MaskFunct7,
            Syntax.RdRs1Shamt, Extension.I,
            new Control(true, false, true, SrcA.Rs1, SrcB.Imm, alu, WbSrc.Alu,
                MemOp.None, 0, false, BranchKind.None, SystemOp.None, false, InstructionClass.Arithmetic));

    private static InstructionDef Register(
        Op op, string mnemonic, uint funct7, uint funct3, AluOp alu, Extension extension) =>
        new(op, mnemonic, Format.R, OpcodeReg | (funct3 << 12) | (funct7 << 25), MaskFunct7,
            Syntax.RdRs1Rs2, extension,
            new Control(true, true, true, SrcA.Rs1, SrcB.Rs2, alu, WbSrc.Alu,
                MemOp.None, 0, false, BranchKind.None, SystemOp.None, false,
                extension == Extension.M ? InstructionClass.MulDiv : InstructionClass.Arithmetic));

    private static InstructionDef Fixed(Op op, string mnemonic, uint word, Extension extension, SystemOp system) =>
        new(op, mnemonic, Format.Fixed, word, MaskAll, Syntax.None, extension,
            Plain(InstructionClass.System) with { System = system });

    private static InstructionDef CsrRegister(Op op, string mnemonic, uint funct3, SystemOp system) =>
        new(op, mnemonic, Format.Csr, OpcodeSystem | (funct3 << 12), MaskFunct3, Syntax.RdCsrRs1, Extension.Zicsr,
            Plain(InstructionClass.System) with
            {
                UsesRs1 = true, WritesRd = true, Wb = WbSrc.Csr, System = system,
            });

    private static InstructionDef CsrImmediate(Op op, string mnemonic, uint funct3, SystemOp system) =>
        new(op, mnemonic, Format.CsrImm, OpcodeSystem | (funct3 << 12), MaskFunct3, Syntax.RdCsrZimm, Extension.Zicsr,
            Plain(InstructionClass.System) with
            {
                WritesRd = true, Wb = WbSrc.Csr, System = system, CsrImmediate = true,
            });
}
