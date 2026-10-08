using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

// The pseudo-instructions: names the assembler accepts that are other instructions underneath.
// They follow the RISC-V assembly manual, so programs written for GNU as, Ripes, Venus or RARS
// mean the same thing here.
internal sealed partial class Assembly
{
    private const int Zero = 0;
    private const int Ra = 1;
    private const int T1 = 6;

    private static void AddPseudoForms(Dictionary<string, List<Form>> forms)
    {
        void One(string usage, K[] shape, Action<EmitContext> emit) =>
            Add(forms, usage.Split(' ')[0], new Form(usage, shape, 1, emit));

        void Two(string usage, K[] shape, Action<EmitContext> emit) =>
            Add(forms, usage.Split(' ')[0], new Form(usage, shape, 2, emit));

        One("nop", [], c => c.Emit(Op.Addi));

        // An instruction that is guaranteed to trap, for marking code that must not be reached.
        One("unimp", [], c => c.Emit(Op.Csrrw, Zero, Zero, 0, Csr.Cycle, c.StatementSpan));
        Add(forms, "li", new Form("li rd, imm", [K.Reg, K.Expr], 2, EmitLi, SizeLi));

        // Without position-independent code, "la" and "lla" are the same thing.
        Two("la rd, symbol", [K.Reg, K.Expr], c => PcRelative(c, 1, c.Reg(0), Op.Addi, c.Reg(0)));
        Two("lla rd, symbol", [K.Reg, K.Expr], c => PcRelative(c, 1, c.Reg(0), Op.Addi, c.Reg(0)));

        // ---- One register in, one out ----
        One("mv rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Addi, c.Reg(0), c.Reg(1), 0, 0, c.StatementSpan));
        One("not rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Xori, c.Reg(0), c.Reg(1), 0, -1, c.StatementSpan));
        One("neg rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Sub, c.Reg(0), Zero, c.Reg(1)));
        One("seqz rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Sltiu, c.Reg(0), c.Reg(1), 0, 1, c.StatementSpan));
        One("snez rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Sltu, c.Reg(0), Zero, c.Reg(1)));
        One("sltz rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Slt, c.Reg(0), c.Reg(1), Zero));
        One("sgtz rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Slt, c.Reg(0), Zero, c.Reg(1)));
        One("zext.b rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Andi, c.Reg(0), c.Reg(1), 0, 255, c.StatementSpan));

        // ---- Branches against zero ----
        One("beqz rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Beq, 0, c.Reg(0), Zero, c.Distance(1), c.Span(1)));
        One("bnez rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Bne, 0, c.Reg(0), Zero, c.Distance(1), c.Span(1)));
        One("bgez rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Bge, 0, c.Reg(0), Zero, c.Distance(1), c.Span(1)));
        One("bltz rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Blt, 0, c.Reg(0), Zero, c.Distance(1), c.Span(1)));
        One("blez rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Bge, 0, Zero, c.Reg(0), c.Distance(1), c.Span(1)));
        One("bgtz rs, label", [K.Reg, K.Expr], c => c.Emit(Op.Blt, 0, Zero, c.Reg(0), c.Distance(1), c.Span(1)));

        // ---- The comparisons the hardware lacks: the same branch with its operands swapped ----
        One("bgt rs, rt, label", [K.Reg, K.Reg, K.Expr],
            c => c.Emit(Op.Blt, 0, c.Reg(1), c.Reg(0), c.Distance(2), c.Span(2)));
        One("ble rs, rt, label", [K.Reg, K.Reg, K.Expr],
            c => c.Emit(Op.Bge, 0, c.Reg(1), c.Reg(0), c.Distance(2), c.Span(2)));
        One("bgtu rs, rt, label", [K.Reg, K.Reg, K.Expr],
            c => c.Emit(Op.Bltu, 0, c.Reg(1), c.Reg(0), c.Distance(2), c.Span(2)));
        One("bleu rs, rt, label", [K.Reg, K.Reg, K.Expr],
            c => c.Emit(Op.Bgeu, 0, c.Reg(1), c.Reg(0), c.Distance(2), c.Span(2)));

        // ---- Jumps, calls and returns ----
        One("j label", [K.Expr], c => c.Emit(Op.Jal, Zero, 0, 0, c.Distance(0), c.Span(0)));
        One("jal label", [K.Expr], c => c.Emit(Op.Jal, Ra, 0, 0, c.Distance(0), c.Span(0)));
        One("jr rs", [K.Reg], c => c.Emit(Op.Jalr, Zero, c.Reg(0), 0, 0, c.StatementSpan));
        One("jalr rs", [K.Reg], c => c.Emit(Op.Jalr, Ra, c.Reg(0), 0, 0, c.StatementSpan));
        One("jalr rd, rs", [K.Reg, K.Reg], c => c.Emit(Op.Jalr, c.Reg(0), c.Reg(1), 0, 0, c.StatementSpan));
        One("jalr rd, rs, offset", [K.Reg, K.Reg, K.Expr],
            c => c.Emit(Op.Jalr, c.Reg(0), c.Reg(1), 0, c.Value(2), c.Span(2)));
        One("ret", [], c => c.Emit(Op.Jalr, Zero, Ra, 0, 0, c.StatementSpan));

        // A call reaches anywhere: auipc builds the upper part of the distance in ra itself, and
        // jalr adds the lower part while replacing ra with the return address. A tail call must
        // leave ra alone, so it borrows t1.
        Two("call label", [K.Expr], c => PcRelative(c, 0, Ra, Op.Jalr, Ra));
        Two("tail label", [K.Expr], c => PcRelative(c, 0, T1, Op.Jalr, Zero));

        // ---- CSR access ----
        One("csrr rd, csr", [K.Reg, K.Expr], c => c.Emit(Op.Csrrs, c.Reg(0), Zero, 0, c.CsrNumber(1), c.Span(1)));
        One("csrw csr, rs", [K.Expr, K.Reg], c => c.Emit(Op.Csrrw, Zero, c.Reg(1), 0, c.CsrNumber(0), c.Span(0)));
        One("csrs csr, rs", [K.Expr, K.Reg], c => c.Emit(Op.Csrrs, Zero, c.Reg(1), 0, c.CsrNumber(0), c.Span(0)));
        One("csrc csr, rs", [K.Expr, K.Reg], c => c.Emit(Op.Csrrc, Zero, c.Reg(1), 0, c.CsrNumber(0), c.Span(0)));
        One("csrwi csr, imm5", [K.Expr, K.Expr], c => CsrImmediate(c, Op.Csrrwi));
        One("csrsi csr, imm5", [K.Expr, K.Expr], c => CsrImmediate(c, Op.Csrrsi));
        One("csrci csr, imm5", [K.Expr, K.Expr], c => CsrImmediate(c, Op.Csrrci));

        // ---- Reading the counters ----
        One("rdcycle rd", [K.Reg], c => c.Emit(Op.Csrrs, c.Reg(0), Zero, 0, Csr.Cycle, c.StatementSpan));
        One("rdinstret rd", [K.Reg], c => c.Emit(Op.Csrrs, c.Reg(0), Zero, 0, Csr.Instret, c.StatementSpan));
        One("rdcycleh rd", [K.Reg], c => c.Emit(Op.Csrrs, c.Reg(0), Zero, 0, Csr.Cycleh, c.StatementSpan));
        One("rdinstreth rd", [K.Reg], c => c.Emit(Op.Csrrs, c.Reg(0), Zero, 0, Csr.Instreth, c.StatementSpan));
    }

    private static void CsrImmediate(EmitContext c, Op op)
    {
        var constant = c.Zimm(1);
        var csr = c.CsrNumber(0);
        c.Emit(op, Zero, (int)(constant ?? 0), 0, constant is null ? null : csr, c.Span(0));
    }

    // "li" must have a size before addresses exist, so the first pass decides it from what it
    // can see: a constant it can already compute is one instruction when that is enough, and
    // everything else (a forward reference, an address) is two.
    private static int SizeLi(Assembly assembly, Statement statement)
    {
        var expression = ((ExprOperand)statement.Operands[1]).Value;
        if (!assembly.TryEvaluateEarly(expression, out var value) || value is < int.MinValue or > uint.MaxValue)
        {
            return 2;
        }

        var word = (int)value;
        return FitsImmediate(word) || (word & 0xFFF) == 0 ? 1 : 2;
    }

    private static void EmitLi(EmitContext c)
    {
        if (c.Value(1) is not { } value)
        {
            return;
        }

        // 0xffffffff and -1 are the same 32 bits, so both are accepted.
        if (value is < int.MinValue or > uint.MaxValue)
        {
            c.Error(c.Span(1), $"{value} does not fit in 32 bits");
            return;
        }

        var rd = c.Reg(0);
        var word = (int)value;
        var (high, low) = Split(word);

        if (c.Count == 1 && FitsImmediate(word))
        {
            c.Emit(Op.Addi, rd, Zero, 0, word, c.Span(1));
        }
        else if (c.Count == 1)
        {
            c.Emit(Op.Lui, rd, 0, 0, high, c.Span(1));
        }
        else
        {
            c.Emit(Op.Lui, rd, 0, 0, high, c.Span(1));
            c.Emit(Op.Addi, rd, rd, 0, low, c.Span(1));
        }
    }

    /// <summary>
    /// Writes <c>auipc</c> and a second instruction that between them reach the operand from here:
    /// the first holds the upper part of the distance, the second adds the lower part.
    /// </summary>
    private static void PcRelative(EmitContext c, int operand, int upperRegister, Op second, int secondRd)
    {
        // The distance is measured from the auipc, which is the instruction about to be written.
        if (c.Distance(operand) is not { } distance)
        {
            return;
        }

        var (high, low) = Split((int)distance);
        c.Emit(Op.Auipc, upperRegister, 0, 0, high, c.Span(operand));
        c.Emit(second, secondRd, upperRegister, 0, low, c.Span(operand));
    }

    private static bool FitsImmediate(int value) => value is >= -2048 and <= 2047;

    /// <summary>
    /// Splits a word into the part <c>lui</c> or <c>auipc</c> loads and the part an I-type adds.
    /// The lower part is signed, so when its top bit is set the upper part is one more.
    /// </summary>
    private static (int High, int Low) Split(int word)
    {
        var low = (word << 20) >> 20;
        return (unchecked(word - low), low);
    }
}
