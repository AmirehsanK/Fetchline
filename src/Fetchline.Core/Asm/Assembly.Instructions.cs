using System.Buffers.Binary;
using System.Globalization;
using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

// Instructions: how a statement is matched to a form, and how a form writes machine words.
internal sealed partial class Assembly
{
    private static readonly Dictionary<string, Form[]> Forms = BuildForms();

    public static IReadOnlyCollection<string> Mnemonics => Forms.Keys;

    /// <summary>The kinds of operand a form can ask for.</summary>
    private enum K : byte
    {
        /// <summary>A register.</summary>
        Reg,

        /// <summary>An offset and a base register.</summary>
        Mem,

        /// <summary>An expression: an immediate, a label, a CSR, a fence set.</summary>
        Expr,
    }

    private static Dictionary<string, Form[]> BuildForms()
    {
        var forms = new Dictionary<string, List<Form>>(StringComparer.Ordinal);
        AddRealForms(forms);
        AddPseudoForms(forms);
        return forms.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    private void Instruction(Statement statement)
    {
        if (!Forms.TryGetValue(statement.Name, out var forms))
        {
            _diagnostics.Error(
                statement.NameSpan,
                $"unknown instruction '{statement.Name}'",
                Suggest.Hint(statement.Name, Forms.Keys));
            return;
        }

        if (_current != _text)
        {
            _diagnostics.Error(
                statement.NameSpan, $"an instruction in {_current.Name}", "code belongs in .text; put .text before it");
            return;
        }

        if (_current.Size % 4 != 0)
        {
            _diagnostics.Error(
                statement.NameSpan,
                $"this instruction would start at offset {_current.Size}, which is not a multiple of 4",
                "put .align 2 before it");
            return;
        }

        var form = Array.Find(forms, candidate => Matches(candidate, statement.Operands));
        if (form is null)
        {
            if (MisspelledRegister(forms, statement.Operands))
            {
                return;
            }

            _diagnostics.Error(
                statement.Span,
                $"'{statement.Name}' does not take these operands",
                "usage: " + string.Join("  or  ", forms.Select(candidate => candidate.Usage)));
            return;
        }

        var count = form.Size?.Invoke(this, statement) ?? form.Count;
        Place(new InstructionItem(statement, form, count), (uint)(4 * count));
    }

    // A name where a register belongs is nearly always a register spelled wrong ("a8", "A0"),
    // and saying so is more use than listing the operands the instruction takes.
    private bool MisspelledRegister(Form[] forms, IReadOnlyList<Operand> operands)
    {
        foreach (var form in forms.Where(candidate => candidate.Shape.Length == operands.Count))
        {
            for (var i = 0; i < operands.Count; i++)
            {
                if (form.Shape[i] == K.Reg && operands[i] is ExprOperand { Value: SymbolExpr symbol })
                {
                    _diagnostics.Error(
                        symbol.Span,
                        $"'{symbol.Name}' is not a register",
                        Suggest.Hint(symbol.Name, Registers.AllNames));
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(Form form, IReadOnlyList<Operand> operands)
    {
        if (form.Shape.Length != operands.Count)
        {
            return false;
        }

        for (var i = 0; i < operands.Count; i++)
        {
            var matches = (form.Shape[i], operands[i]) switch
            {
                (K.Reg, RegisterOperand) => true,
                (K.Mem, MemoryOperand) => true,
                (K.Expr, ExprOperand) => true,
                _ => false,
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    // %pcrel_lo names the instruction that holds its %pcrel_hi, and that instruction may come
    // after it. So the distances are collected before anything is written.
    private void CollectPcrelHi()
    {
        _quiet = true;
        foreach (var item in _items.OfType<InstructionItem>())
        {
            EnterItem(item);
            foreach (var operand in item.Statement.Operands)
            {
                var expression = operand switch
                {
                    ExprOperand value => value.Value,
                    MemoryOperand memory => memory.Offset,
                    _ => null,
                };

                if (expression is not null && FindPcrelHi(expression) is { } hi
                    && Evaluator.Evaluate(hi.Operand, this) is { IsAbsolute: true } target)
                {
                    _pcrel[HereAddress] = (int)((uint)target.Number - HereAddress);
                }
            }
        }

        _quiet = false;
    }

    private static RelocExpr? FindPcrelHi(Expr expression) => expression switch
    {
        RelocExpr { Kind: RelocKind.PcrelHi } reloc => reloc,
        RelocExpr reloc => FindPcrelHi(reloc.Operand),
        UnaryExpr unary => FindPcrelHi(unary.Operand),
        BinaryExpr binary => FindPcrelHi(binary.Left) ?? FindPcrelHi(binary.Right),
        _ => null,
    };

    /// <summary>One way of writing an instruction: the operands it takes and the words it becomes.</summary>
    /// <param name="Usage">How to write it, for the hint when the operands are wrong.</param>
    /// <param name="Count">How many machine instructions it becomes, when that never varies.</param>
    /// <param name="Size">Works the count out in the first pass, for <c>li</c>, whose size depends on its value.</param>
    private sealed record Form(
        string Usage, K[] Shape, int Count, Action<EmitContext> Emit, Func<Assembly, Statement, int>? Size = null);

    /// <summary>What a form writes with, in the second pass.</summary>
    private sealed class EmitContext(Assembly assembly, InstructionItem item)
    {
        private int _emitted;

        public IReadOnlyList<Operand> Operands => item.Statement.Operands;

        /// <summary>How many machine instructions the first pass reserved for this statement.</summary>
        public int Count => item.Count;

        /// <summary>The address of the machine instruction about to be written.</summary>
        public uint Pc => item.Section.Base + item.Offset + (uint)(4 * _emitted);

        public SourceSpan StatementSpan => item.Statement.Span;

        public int Reg(int index) => ((RegisterOperand)Operands[index]).Register;

        public SourceSpan Span(int index) => Operands[index].Span;

        /// <summary>The value of an expression operand, or null when it has none (already reported).</summary>
        public long? Value(int index) => assembly.EvaluateFinal(((ExprOperand)Operands[index]).Value);

        /// <summary>The value of an operand the first pass could already compute, if it could.</summary>
        public (int Base, long? Offset) Mem(int index)
        {
            var memory = (MemoryOperand)Operands[index];
            return (memory.Base, memory.Offset is null ? 0 : assembly.EvaluateFinal(memory.Offset));
        }

        /// <summary>The distance from the instruction about to be written to a label or address.</summary>
        public long? Distance(int index)
        {
            if (Value(index) is not { } target)
            {
                return null;
            }

            if (target is < int.MinValue or > uint.MaxValue)
            {
                Error(Span(index), $"{target} is not a 32-bit address");
                return null;
            }

            // Addresses wrap around, as they do in the hardware: -4 from address 0 is 0xfffffffc.
            return (int)((uint)target - Pc);
        }

        /// <summary>A CSR written by name or as a number.</summary>
        public long? CsrNumber(int index)
        {
            if (Operands[index] is ExprOperand { Value: SymbolExpr symbol })
            {
                if (Csr.TryGetNumber(symbol.Name, out var number))
                {
                    return number;
                }

                if (!assembly._symbols.ContainsKey(symbol.Name))
                {
                    Error(symbol.Span, $"unknown CSR '{symbol.Name}'", Suggest.Hint(symbol.Name, Csr.Names.Keys));
                    return null;
                }
            }

            return Value(index);
        }

        /// <summary>The operand of lui and auipc: twenty bits, returned already shifted into place.</summary>
        public long? Upper(int index)
        {
            if (Value(index) is not { } value)
            {
                return null;
            }

            if (value is < 0 or > 0xFFFFF)
            {
                Error(
                    Span(index),
                    $"{value} does not fit in 20 bits (0 to 1048575)",
                    "to load the upper part of a constant or an address, write %hi(...)");
                return null;
            }

            return (int)(value << 12);
        }

        /// <summary>The five-bit constant of a CSR-immediate instruction.</summary>
        public long? Zimm(int index)
        {
            if (Value(index) is not { } value)
            {
                return null;
            }

            if (value is < 0 or > 31)
            {
                Error(Span(index), $"a CSR immediate is 0 to 31, not {value}", "put a larger value in a register first");
                return null;
            }

            return value;
        }

        /// <summary>A fence set: some of the letters i, o, r, w, in that order.</summary>
        public long? FenceSet(int index)
        {
            if (Operands[index] is ExprOperand { Value: SymbolExpr symbol })
            {
                var bits = 0;
                var next = 0;
                foreach (var letter in symbol.Name)
                {
                    var position = "iorw".IndexOf(letter, next);
                    if (position < 0)
                    {
                        bits = 0;
                        break;
                    }

                    bits |= 8 >> position;
                    next = position + 1;
                }

                if (bits != 0)
                {
                    return bits;
                }
            }

            Error(Span(index), "a fence set is some of the letters i, o, r, w, in that order", "for example: fence rw, rw");
            return null;
        }

        public void Error(SourceSpan span, string message, string? hint = null) =>
            assembly._diagnostics.Error(span, message, hint);

        /// <summary>
        /// Writes one machine instruction. A null immediate means its operand had no value, which
        /// was reported where it was found; the slot is skipped so that later ones keep their place.
        /// </summary>
        /// <param name="at">The operand the immediate came from, for a message when it does not fit.</param>
        public void Emit(Op op, int rd, int rs1, int rs2, long? immediate, SourceSpan at)
        {
            var slot = _emitted++;
            if (immediate is not { } value || slot >= item.Count)
            {
                return;
            }

            var def = InstructionSet.Get(op);
            var (min, max) = Encoder.ImmediateRange(def.Format);
            if (def.Format is not (Format.R or Format.Fixed) && (value < min || value > max))
            {
                Error(at, RangeMessage(def, value), RangeHint(def, value));
                return;
            }

            if (!Encoder.TryEncode(op, rd, rs1, rs2, (int)value, out var word, out var error))
            {
                Error(at, error == EncodeError.OffsetIsOdd
                    ? $"the target is {Signed(value)} bytes away, and an odd distance cannot be encoded"
                    : $"cannot encode '{def.Mnemonic}': {error}");
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                item.Section.Bytes.AsSpan((int)item.Offset + (4 * slot)), word);
        }

        /// <summary>Writes an instruction with no immediate.</summary>
        public void Emit(Op op, int rd = 0, int rs1 = 0, int rs2 = 0) => Emit(op, rd, rs1, rs2, 0, StatementSpan);

        private static string RangeMessage(InstructionDef def, long value) => def.Format switch
        {
            Format.I or Format.S => $"{value} does not fit in 12 signed bits (-2048 to 2047)",
            Format.Shift => $"a shift amount is 0 to 31, not {value}",
            Format.B => $"the target is {Signed(value)} bytes away; a branch reaches -4096 to +4094",
            Format.J => $"the target is {Signed(value)} bytes away; a jump reaches -1048576 to +1048574",
            _ => $"a CSR number is 0 to 4095, not {value}",
        };

        private static string? RangeHint(InstructionDef def, long value) => def switch
        {
            { Op: Op.Addi } => "use li to load a constant of any size",
            { Format: Format.I } when value is >= 2048 and <= 4095 =>
                $"as 12 bits that is {value - 4096}; write {value - 4096} if those bits are what you mean",
            { Format: Format.B } => "branch the other way around a 'j', which reaches further",
            _ => null,
        };

        private static string Signed(long value) => value.ToString("+0;-0", CultureInfo.InvariantCulture);
    }
}
