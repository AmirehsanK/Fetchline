namespace Fetchline.Core.Asm;

public enum UnaryOp : byte
{
    Negate,
    Plus,
    Complement,
}

public enum BinaryOp : byte
{
    Add, Subtract, Multiply, Divide, Remainder,
    ShiftLeft, ShiftRight, And, Or, Xor,
}

/// <summary>The relocation operators: ways of taking part of an address.</summary>
public enum RelocKind : byte
{
    /// <summary><c>%hi(x)</c>: the upper 20 bits of <c>x</c>, adjusted for the sign of <c>%lo(x)</c>.</summary>
    Hi,

    /// <summary><c>%lo(x)</c>: the low 12 bits of <c>x</c>, sign-extended.</summary>
    Lo,

    /// <summary><c>%pcrel_hi(x)</c>: the upper 20 bits of the distance from this instruction to <c>x</c>.</summary>
    PcrelHi,

    /// <summary>
    /// <c>%pcrel_lo(label)</c>: the low 12 bits that go with the <c>%pcrel_hi</c> of the
    /// instruction at <c>label</c>. Its operand names that instruction, not the target.
    /// </summary>
    PcrelLo,
}

/// <summary>An expression in an operand or a directive, as written.</summary>
public abstract record Expr(SourceSpan Span);

public sealed record NumberExpr(long Value, SourceSpan Span) : Expr(Span);

public sealed record SymbolExpr(string Name, SourceSpan Span) : Expr(Span);

/// <summary><c>1b</c> or <c>1f</c>: the nearest local label <c>1:</c> before or after this point.</summary>
public sealed record LocalRefExpr(int Number, bool Forward, SourceSpan Span) : Expr(Span);

/// <summary><c>.</c>: the address of the statement the expression is in.</summary>
public sealed record HereExpr(SourceSpan Span) : Expr(Span);

public sealed record UnaryExpr(UnaryOp Op, Expr Operand, SourceSpan Span) : Expr(Span);

public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right, SourceSpan Span) : Expr(Span);

public sealed record RelocExpr(RelocKind Kind, Expr Operand, SourceSpan Span) : Expr(Span);

public enum ValueKind : byte
{
    /// <summary>Not known yet (first pass), or wrong in a way that has been reported (second pass).</summary>
    Unknown,

    /// <summary>A plain number.</summary>
    Absolute,

    /// <summary>
    /// An offset into a section whose address is not fixed yet. Only the first pass sees these;
    /// by the second pass every section has an address and every value is absolute.
    /// </summary>
    Relative,
}

/// <summary>
/// What an expression evaluates to. During the first pass the assembler knows where a label is
/// within its section but not where the section will be, so a label is <see cref="ValueKind.Relative"/>
/// and only some arithmetic on it has a known result: adding a number keeps it relative, and the
/// difference of two labels in one section is a plain number. That is exactly what is needed to
/// size <c>li a2, end - start</c> before the layout is final.
/// </summary>
public readonly record struct Value(ValueKind Kind, long Number, int Section = -1)
{
    public static Value Unknown => default;

    public bool IsAbsolute => Kind == ValueKind.Absolute;

    public static Value Absolute(long number) => new(ValueKind.Absolute, number);

    public static Value Relative(int section, long offset) => new(ValueKind.Relative, offset, section);
}
