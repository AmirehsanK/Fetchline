namespace Fetchline.Core.Asm;

/// <summary>One operand of an instruction or a directive, as written.</summary>
public abstract record Operand(SourceSpan Span);

/// <summary>A register: <c>a0</c>, <c>x10</c>.</summary>
public sealed record RegisterOperand(int Register, SourceSpan Span) : Operand(Span);

/// <summary>An offset and a base register: <c>8(sp)</c>, <c>(a0)</c>, <c>%lo(msg)(a0)</c>.</summary>
/// <param name="Offset">Null when only the base is written, which means zero.</param>
public sealed record MemoryOperand(Expr? Offset, int Base, SourceSpan Span) : Operand(Span);

/// <summary>
/// Anything that is not a register, a memory reference or a string: an immediate, a label, a CSR
/// name, a fence set. What it means is decided by the instruction it belongs to.
/// </summary>
public sealed record ExprOperand(Expr Value, SourceSpan Span) : Operand(Span);

/// <summary>A quoted string, for the directives that take one.</summary>
public sealed record StringOperand(byte[] Bytes, SourceSpan Span) : Operand(Span);

/// <summary>A label in front of a statement.</summary>
/// <param name="Name">The label's name; for a numeric local label, its digits.</param>
/// <param name="LocalNumber">The number of a local label such as <c>1:</c>, or -1 for a named one.</param>
public sealed record LabelDefinition(string Name, int LocalNumber, SourceSpan Span)
{
    public bool IsLocal => LocalNumber >= 0;
}

public enum StatementKind : byte
{
    /// <summary>Labels only, or nothing.</summary>
    Empty,

    /// <summary>A mnemonic and its operands. The name is lower case.</summary>
    Instruction,

    /// <summary>A directive and its operands. The name is lower case and starts with a dot.</summary>
    Directive,

    /// <summary><c>name = expression</c>; the name is the symbol and the one operand is its value.</summary>
    Assignment,
}

/// <summary>One statement: the labels in front of it, then an instruction, a directive or nothing.</summary>
/// <param name="NameSpan">Where the mnemonic, directive or assigned symbol is.</param>
/// <param name="Span">The statement from its name to its last operand, without its labels.</param>
/// <param name="IsBroken">The statement did not parse. It has been reported and must be skipped.</param>
public sealed record Statement(
    IReadOnlyList<LabelDefinition> Labels,
    StatementKind Kind,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<Operand> Operands,
    SourceSpan Span,
    bool IsBroken = false);
