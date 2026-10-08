using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

// The real instructions. Their forms are generated from the instruction table: the operand
// pattern of a row says what its assembly looks like, so no mnemonic is spelled out here.
internal sealed partial class Assembly
{
    private static void AddRealForms(Dictionary<string, List<Form>> forms)
    {
        foreach (var def in InstructionSet.All)
        {
            var op = def.Op;
            var name = def.Mnemonic;
            switch (def.Syntax)
            {
                case Syntax.None:
                    Add(forms, name, new Form(name, [], 1, c => c.Emit(op)));
                    break;

                case Syntax.RdRs1Rs2:
                    Add(forms, name, new Form($"{name} rd, rs1, rs2", [K.Reg, K.Reg, K.Reg], 1,
                        c => c.Emit(op, c.Reg(0), c.Reg(1), c.Reg(2))));
                    break;

                case Syntax.RdRs1Imm or Syntax.RdRs1Shamt:
                    Add(forms, name, new Form(
                        $"{name} rd, rs1, {(def.Syntax == Syntax.RdRs1Imm ? "imm" : "shamt")}", [K.Reg, K.Reg, K.Expr], 1,
                        c => c.Emit(op, c.Reg(0), c.Reg(1), 0, c.Value(2), c.Span(2))));
                    break;

                case Syntax.RdMem:
                    Add(forms, name, new Form($"{name} rd, offset(rs1)", [K.Reg, K.Mem], 1, c =>
                    {
                        var (baseRegister, offset) = c.Mem(1);
                        c.Emit(op, c.Reg(0), baseRegister, 0, offset, c.Span(1));
                    }));
                    break;

                case Syntax.Rs2Mem:
                    Add(forms, name, new Form($"{name} rs2, offset(rs1)", [K.Reg, K.Mem], 1, c =>
                    {
                        var (baseRegister, offset) = c.Mem(1);
                        c.Emit(op, 0, baseRegister, c.Reg(0), offset, c.Span(1));
                    }));
                    break;

                case Syntax.Rs1Rs2Target:
                    Add(forms, name, new Form($"{name} rs1, rs2, label", [K.Reg, K.Reg, K.Expr], 1,
                        c => c.Emit(op, 0, c.Reg(0), c.Reg(1), c.Distance(2), c.Span(2))));
                    break;

                case Syntax.RdTarget:
                    Add(forms, name, new Form($"{name} rd, label", [K.Reg, K.Expr], 1,
                        c => c.Emit(op, c.Reg(0), 0, 0, c.Distance(1), c.Span(1))));
                    break;

                case Syntax.RdUpper:
                    Add(forms, name, new Form($"{name} rd, imm20", [K.Reg, K.Expr], 1,
                        c => c.Emit(op, c.Reg(0), 0, 0, c.Upper(1), c.Span(1))));
                    break;

                case Syntax.RdCsrRs1:
                    Add(forms, name, new Form($"{name} rd, csr, rs1", [K.Reg, K.Expr, K.Reg], 1,
                        c => c.Emit(op, c.Reg(0), c.Reg(2), 0, c.CsrNumber(1), c.Span(1))));
                    break;

                case Syntax.RdCsrZimm:
                    Add(forms, name, new Form($"{name} rd, csr, imm5", [K.Reg, K.Expr, K.Expr], 1, c =>
                    {
                        // The five-bit constant travels in the rs1 field.
                        var constant = c.Zimm(2);
                        var csr = c.CsrNumber(1);
                        c.Emit(op, c.Reg(0), (int)(constant ?? 0), 0, constant is null ? null : csr, c.Span(1));
                    }));
                    break;

                case Syntax.Fence:
                    // A bare "fence" orders everything against everything.
                    Add(forms, name, new Form(name, [], 1, c => c.Emit(op, 0, 0, 0, 0x0FF, c.StatementSpan)));
                    Add(forms, name, new Form($"{name} pred, succ", [K.Expr, K.Expr], 1, c =>
                    {
                        var (before, after) = (c.FenceSet(0), c.FenceSet(1));
                        c.Emit(op, 0, 0, 0, (before << 4) | after, c.StatementSpan);
                    }));
                    break;
            }
        }
    }

    private static void Add(Dictionary<string, List<Form>> forms, string mnemonic, Form form)
    {
        if (!forms.TryGetValue(mnemonic, out var list))
        {
            forms.Add(mnemonic, list = []);
        }

        list.Add(form);
    }
}
