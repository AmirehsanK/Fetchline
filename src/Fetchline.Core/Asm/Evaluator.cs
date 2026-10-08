namespace Fetchline.Core.Asm;

/// <summary>What an expression is evaluated against: the symbols, the current address, the pass.</summary>
internal interface IExprScope
{
    /// <summary>
    /// False in the first pass, where a value that cannot be worked out yet is simply unknown.
    /// True in the second, where it is an error and gets reported.
    /// </summary>
    bool Final { get; }

    DiagnosticBag Diagnostics { get; }

    /// <summary>The address of the statement being evaluated.</summary>
    Value Here { get; }

    /// <summary>The value of a symbol. In the final pass an undefined one is reported here.</summary>
    Value Lookup(SymbolExpr symbol);

    Value LookupLocal(LocalRefExpr reference);

    /// <summary>Applies a relocation operator. These depend on final addresses.</summary>
    Value Reloc(RelocExpr expression, Value operand);
}

/// <summary>Evaluates expressions in 64-bit signed arithmetic, as GNU <c>as</c> does.</summary>
internal static class Evaluator
{
    public static Value Evaluate(Expr expression, IExprScope scope) => expression switch
    {
        NumberExpr number => Value.Absolute(number.Value),
        SymbolExpr symbol => scope.Lookup(symbol),
        LocalRefExpr local => scope.LookupLocal(local),
        HereExpr => scope.Here,
        UnaryExpr unary => Unary(unary, Evaluate(unary.Operand, scope)),
        BinaryExpr binary => Binary(binary, Evaluate(binary.Left, scope), Evaluate(binary.Right, scope), scope),
        RelocExpr reloc => Reloc(reloc, scope),
        _ => Value.Unknown,
    };

    private static Value Reloc(RelocExpr reloc, IExprScope scope)
    {
        var operand = Evaluate(reloc.Operand, scope);
        return operand.Kind == ValueKind.Unknown ? Value.Unknown : scope.Reloc(reloc, operand);
    }

    private static Value Unary(UnaryExpr unary, Value operand)
    {
        if (!operand.IsAbsolute)
        {
            // The negative or the complement of an address in an unplaced section means nothing yet.
            return unary.Op == UnaryOp.Plus ? operand : Value.Unknown;
        }

        return Value.Absolute(unary.Op switch
        {
            UnaryOp.Negate => unchecked(-operand.Number),
            UnaryOp.Complement => ~operand.Number,
            _ => operand.Number,
        });
    }

    private static Value Binary(BinaryExpr binary, Value left, Value right, IExprScope scope)
    {
        if (left.Kind == ValueKind.Unknown || right.Kind == ValueKind.Unknown)
        {
            return Value.Unknown;
        }

        if (left.IsAbsolute && right.IsAbsolute)
        {
            return Arithmetic(binary, left.Number, right.Number, scope);
        }

        // At least one side is an offset into a section that has no address yet.
        return (binary.Op, left.Kind, right.Kind) switch
        {
            (BinaryOp.Add, ValueKind.Relative, ValueKind.Absolute) =>
                Value.Relative(left.Section, unchecked(left.Number + right.Number)),
            (BinaryOp.Add, ValueKind.Absolute, ValueKind.Relative) =>
                Value.Relative(right.Section, unchecked(left.Number + right.Number)),
            (BinaryOp.Subtract, ValueKind.Relative, ValueKind.Absolute) =>
                Value.Relative(left.Section, unchecked(left.Number - right.Number)),
            (BinaryOp.Subtract, ValueKind.Relative, ValueKind.Relative) when left.Section == right.Section =>
                Value.Absolute(unchecked(left.Number - right.Number)),
            _ => Value.Unknown,
        };
    }

    private static Value Arithmetic(BinaryExpr binary, long left, long right, IExprScope scope)
    {
        switch (binary.Op)
        {
            case BinaryOp.Divide or BinaryOp.Remainder when right == 0:
                Report(scope, binary.Right.Span, "division by zero");
                return Value.Unknown;

            case BinaryOp.ShiftLeft or BinaryOp.ShiftRight when right is < 0 or > 63:
                Report(scope, binary.Right.Span, $"a shift by {right} is outside 0 to 63");
                return Value.Unknown;
        }

        return Value.Absolute(unchecked(binary.Op switch
        {
            BinaryOp.Add => left + right,
            BinaryOp.Subtract => left - right,
            BinaryOp.Multiply => left * right,
            // long.MinValue / -1 overflows; the wrapped answer is the dividend itself.
            BinaryOp.Divide => right == -1 ? -left : left / right,
            BinaryOp.Remainder => right == -1 ? 0 : left % right,
            BinaryOp.ShiftLeft => left << (int)right,
            BinaryOp.ShiftRight => left >> (int)right,
            BinaryOp.And => left & right,
            BinaryOp.Or => left | right,
            _ => left ^ right,
        }));
    }

    private static void Report(IExprScope scope, SourceSpan span, string message)
    {
        // The first pass evaluates the same expressions again in the second; report once.
        if (scope.Final)
        {
            scope.Diagnostics.Error(span, message);
        }
    }
}
