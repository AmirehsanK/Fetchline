using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

// Symbols, and the assembler's side of expression evaluation.
internal sealed partial class Assembly
{
    private readonly Dictionary<string, SymbolEntry> _symbols = new(StringComparer.Ordinal);
    private readonly List<LocalLabel> _locals = [];
    private readonly HashSet<string> _globals = new(StringComparer.Ordinal);

    bool IExprScope.Final => _final;

    // The pass that collects %pcrel_hi evaluates expressions the second pass evaluates again; its
    // complaints would be duplicates, so they go nowhere.
    DiagnosticBag IExprScope.Diagnostics => _quiet ? new DiagnosticBag() : _diagnostics;

    Value IExprScope.Here => Address(_hereSection, _hereOffset);

    private uint HereAddress => _hereSection.Base + _hereOffset;

    private void DefineLabel(LabelDefinition label)
    {
        if (label.IsLocal)
        {
            _locals.Add(new LocalLabel(label.LocalNumber, _statementIndex, _current, _current.Size));
            return;
        }

        if (TryAddSymbol(label.Name, label.Span) is { } entry)
        {
            entry.IsLabel = true;
            entry.Section = _current;
            entry.Offset = _current.Size;
        }
    }

    private void DefineConstant(string name, SourceSpan nameSpan, Expr value)
    {
        if (Registers.TryParse(name, out _))
        {
            _diagnostics.Error(nameSpan, $"'{name}' is a register and cannot be a symbol");
            return;
        }

        if (TryAddSymbol(name, nameSpan) is { } entry)
        {
            entry.Expression = value;
            entry.Section = _hereSection;
            entry.Offset = _hereOffset;
            entry.StatementIndex = _statementIndex;
            entry.FirstPassValue = Evaluator.Evaluate(value, this);
        }
    }

    private SymbolEntry? TryAddSymbol(string name, SourceSpan span)
    {
        if (_symbols.TryGetValue(name, out var existing))
        {
            _diagnostics.Error(
                span, $"'{name}' is already defined", $"it was first defined on line {existing.DefinedAt.Line}");
            return null;
        }

        var entry = new SymbolEntry(name, span);
        _symbols.Add(name, entry);
        return entry;
    }

    /// <summary>An address in the second pass; an offset into its section in the first.</summary>
    private Value Address(Section section, uint offset) =>
        _final ? Value.Absolute(section.Base + offset) : Value.Relative(section.Index, offset);

    Value IExprScope.Lookup(SymbolExpr symbol)
    {
        if (!_symbols.TryGetValue(symbol.Name, out var entry))
        {
            // In the first pass this is only a symbol that has not been defined yet.
            ScopeError(symbol.Span, $"undefined symbol '{symbol.Name}'", Suggest.Hint(symbol.Name, _symbols.Keys));
            return Value.Unknown;
        }

        if (entry.IsLabel)
        {
            return Address(entry.Section!, entry.Offset);
        }

        return _final ? ResolveConstant(entry, symbol.Span) : entry.FirstPassValue;
    }

    Value IExprScope.LookupLocal(LocalRefExpr reference)
    {
        LocalLabel? found = null;
        foreach (var local in _locals)
        {
            if (local.Number != reference.Number)
            {
                continue;
            }

            // A label on the statement itself is behind it: "1: j 1b" jumps to itself.
            if (reference.Forward && local.StatementIndex > _statementIndex)
            {
                found = local;
                break;
            }

            if (!reference.Forward && local.StatementIndex <= _statementIndex)
            {
                found = local;
            }
        }

        if (found is { } label)
        {
            return Address(label.Section, label.Offset);
        }

        ScopeError(
            reference.Span,
            $"there is no label '{reference.Number}:' {(reference.Forward ? "after" : "before")} this point");
        return Value.Unknown;
    }

    Value IExprScope.Reloc(RelocExpr expression, Value operand)
    {
        if (!_final || !operand.IsAbsolute)
        {
            return Value.Unknown;
        }

        var value = operand.Number;
        switch (expression.Kind)
        {
            case RelocKind.Hi:
                return Value.Absolute(UpperPart(value));
            case RelocKind.Lo:
                return Value.Absolute(LowerPart(value));
            case RelocKind.PcrelHi:
                return Value.Absolute(UpperPart((int)((uint)value - HereAddress)));
            default:
                if (_pcrel.TryGetValue((uint)value, out var distance))
                {
                    return Value.Absolute(LowerPart(distance));
                }

                ScopeError(
                    expression.Operand.Span,
                    "%pcrel_lo takes the label of the instruction that has the matching %pcrel_hi",
                    "1: auipc a0, %pcrel_hi(msg)  then  addi a0, a0, %pcrel_lo(1b)");
                return Value.Unknown;
        }
    }

    /// <summary>The low twelve bits as the signed number an I-type immediate will be read as.</summary>
    private static long LowerPart(long value) => ((value & 0xFFF) ^ 0x800) - 0x800;

    /// <summary>The upper twenty bits, plus one when the lower part is negative, so the two add up.</summary>
    private static long UpperPart(long value) => ((value + 0x800) >> 12) & 0xFFFFF;

    private void ScopeError(SourceSpan span, string message, string? hint = null)
    {
        if (_final && !_quiet)
        {
            _diagnostics.Error(span, message, hint);
        }
    }

    /// <summary>The value of an expression in the second pass, or null when it has none (reported).</summary>
    private long? EvaluateFinal(Expr expression) =>
        Evaluator.Evaluate(expression, this) is { IsAbsolute: true } value ? value.Number : null;

    /// <summary>Whether an expression is a plain number that the first pass can already compute.</summary>
    private bool TryEvaluateEarly(Expr expression, out long value)
    {
        var result = Evaluator.Evaluate(expression, this);
        value = result.Number;
        return result.IsAbsolute;
    }

    // A constant may be defined in terms of symbols that come after it, so its final value is
    // worked out on first use, in the place it was defined: "." and "1b" mean what they meant there.
    private Value ResolveConstant(SymbolEntry entry, SourceSpan usedAt)
    {
        if (entry.Resolved)
        {
            return entry.HasValue ? Value.Absolute(entry.FinalValue) : Value.Unknown;
        }

        if (entry.Resolving)
        {
            ScopeError(usedAt, $"'{entry.Name}' is defined in terms of itself");
            return Value.Unknown;
        }

        var (section, offset, index) = (_hereSection, _hereOffset, _statementIndex);
        (_hereSection, _hereOffset, _statementIndex) = (entry.Section!, entry.Offset, entry.StatementIndex);
        entry.Resolving = true;

        var value = Evaluator.Evaluate(entry.Expression!, this);

        entry.Resolving = false;
        (_hereSection, _hereOffset, _statementIndex) = (section, offset, index);

        // A failure found while diagnostics are off must be found again when they are on.
        if (value.IsAbsolute || !_quiet)
        {
            entry.Resolved = true;
            entry.HasValue = value.IsAbsolute;
            entry.FinalValue = value.Number;
        }

        return value;
    }

    private void ResolveConstants()
    {
        foreach (var entry in _symbols.Values)
        {
            if (entry.IsLabel)
            {
                entry.FinalValue = entry.Section!.Base + entry.Offset;
            }
            else
            {
                ResolveConstant(entry, entry.DefinedAt);
            }
        }
    }

    private sealed class SymbolEntry(string name, SourceSpan definedAt)
    {
        public string Name { get; } = name;

        public SourceSpan DefinedAt { get; } = definedAt;

        public bool IsLabel { get; set; }

        /// <summary>A label's section; for a constant, the section it was defined in.</summary>
        public Section? Section { get; set; }

        public uint Offset { get; set; }

        public int StatementIndex { get; set; }

        /// <summary>What a constant was defined as.</summary>
        public Expr? Expression { get; set; }

        /// <summary>A constant's value as far as the first pass could tell when it was defined.</summary>
        public Value FirstPassValue { get; set; }

        public long FinalValue { get; set; }

        public bool HasValue { get; set; }

        public bool Resolved { get; set; }

        public bool Resolving { get; set; }
    }

    private readonly record struct LocalLabel(int Number, int StatementIndex, Section Section, uint Offset);
}
