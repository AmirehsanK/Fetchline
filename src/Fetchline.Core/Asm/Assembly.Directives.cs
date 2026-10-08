namespace Fetchline.Core.Asm;

// Directives: sections, symbols, data and space.
internal sealed partial class Assembly
{
    private void Directive(Statement statement)
    {
        switch (statement.Name)
        {
            case ".text":
                _current = _text;
                break;
            case ".data":
                _current = _data;
                break;
            case ".rodata":
                _current = _rodata;
                break;
            case ".bss":
                _current = _bss;
                break;
            case ".section":
                SwitchSection((SymbolExpr)((ExprOperand)statement.Operands[0]).Value);
                break;

            case ".globl" or ".global":
                foreach (var operand in statement.Operands)
                {
                    if (SymbolName(operand) is { } name)
                    {
                        _globals.Add(name);
                    }
                }

                break;

            case ".equ" or ".set":
                if (statement.Operands.Count != 2)
                {
                    _diagnostics.Error(statement.Span, $"'{statement.Name}' takes a name and a value: {statement.Name} SIZE, 64");
                }
                else if (SymbolName(statement.Operands[0]) is { } name && Number(statement.Operands[1]) is { } value)
                {
                    DefineConstant(name, statement.Operands[0].Span, value);
                }

                break;

            case ".byte":
                Data(statement, 1);
                break;
            case ".half" or ".2byte" or ".short":
                Data(statement, 2);
                break;
            case ".word" or ".4byte" or ".long" or ".int":
                Data(statement, 4);
                break;
            case ".dword" or ".8byte" or ".quad":
                Data(statement, 8);
                break;

            case ".ascii":
                Strings(statement, terminated: false);
                break;
            case ".asciz" or ".string":
                Strings(statement, terminated: true);
                break;

            case ".zero" or ".space" or ".skip":
                Space(statement);
                break;

            case ".align" or ".p2align":
                Align(statement, exponent: true);
                break;
            case ".balign":
                Align(statement, exponent: false);
                break;

            case ".option" or ".type" or ".size" or ".file" or ".attribute" or ".ident" or ".local" or ".weak":
                break;

            default:
                _diagnostics.Error(
                    statement.NameSpan,
                    $"unknown directive '{statement.Name}'",
                    Suggest.Hint(statement.Name, Assembler.Directives));
                break;
        }
    }

    private void SwitchSection(SymbolExpr name)
    {
        bool Is(string section) =>
            name.Name == section || name.Name.StartsWith(section + ".", StringComparison.Ordinal);

        if (Is(".text"))
        {
            _current = _text;
        }
        else if (Is(".data") || Is(".sdata"))
        {
            _current = _data;
        }
        else if (Is(".rodata") || Is(".srodata"))
        {
            _current = _rodata;
        }
        else if (Is(".bss") || Is(".sbss"))
        {
            _current = _bss;
        }
        else
        {
            _diagnostics.Error(name.Span, $"unknown section '{name.Name}'", "the sections are .text, .data, .rodata and .bss");
        }
    }

    private void Data(Statement statement, int width)
    {
        if (statement.Operands.Count == 0)
        {
            _diagnostics.Error(statement.Span, $"'{statement.Name}' needs at least one value");
            return;
        }

        foreach (var operand in statement.Operands)
        {
            if (Number(operand, operand is StringOperand ? "use .ascii or .asciz for a string" : null) is null)
            {
                return;
            }
        }

        Place(new DataItem(statement, width), (uint)(width * statement.Operands.Count));
    }

    private void Strings(Statement statement, bool terminated)
    {
        if (statement.Operands.Count == 0 || statement.Operands.Any(operand => operand is not StringOperand))
        {
            var wrong = statement.Operands.FirstOrDefault(operand => operand is not StringOperand);
            _diagnostics.Error(wrong?.Span ?? statement.Span, $"'{statement.Name}' takes quoted strings: {statement.Name} \"text\"");
            return;
        }

        var bytes = new List<byte>();
        foreach (var operand in statement.Operands.Cast<StringOperand>())
        {
            bytes.AddRange(operand.Bytes);
            if (terminated)
            {
                bytes.Add(0);
            }
        }

        Place(new BytesItem(statement, [.. bytes]), (uint)bytes.Count);
    }

    private void Space(Statement statement)
    {
        if (statement.Operands.Count is 0 or > 2)
        {
            _diagnostics.Error(statement.Span, $"'{statement.Name}' takes a size, and optionally a fill value");
            return;
        }

        if (EarlyNumber(statement.Operands[0], "the size") is not { } size)
        {
            return;
        }

        long fill = 0;
        if (statement.Operands.Count == 2)
        {
            if (EarlyNumber(statement.Operands[1], "the fill value") is not { } given)
            {
                return;
            }

            fill = given;
        }

        if (size < 0 || fill is < -128 or > 255)
        {
            var bad = size < 0 ? 0 : 1;
            _diagnostics.Error(
                statement.Operands[bad].Span,
                size < 0 ? $"a size cannot be negative ({size})" : $"a fill value is one byte, not {fill}");
            return;
        }

        Place(new FillItem(statement, (byte)fill, nops: false), (uint)Math.Min(size, uint.MaxValue));
    }

    private void Align(Statement statement, bool exponent)
    {
        if (statement.Operands.Count != 1)
        {
            _diagnostics.Error(statement.Span, $"'{statement.Name}' takes one number");
            return;
        }

        if (EarlyNumber(statement.Operands[0], "the alignment") is not { } given)
        {
            return;
        }

        // On RISC-V ".align n" means 2^n bytes, as ".p2align n" does; ".balign n" means n bytes.
        long alignment;
        if (exponent)
        {
            if (given is < 0 or > 16)
            {
                _diagnostics.Error(
                    statement.Operands[0].Span,
                    $"'{statement.Name} {given}' asks for 2^{given} bytes; the exponent is 0 to 16",
                    given is 4 or 8 or 16 or 32 or 64 ? $"for {given} bytes write .balign {given}" : null);
                return;
            }

            alignment = 1L << (int)given;
        }
        else
        {
            if (given is < 1 or > 65536 || (given & (given - 1)) != 0)
            {
                _diagnostics.Error(statement.Operands[0].Span, $"an alignment is a power of two up to 65536, not {given}");
                return;
            }

            alignment = given;
        }

        var offset = _current.Size;
        var padding = (uint)((alignment - (offset % alignment)) % alignment);
        _current.Alignment = Math.Max(_current.Alignment, (uint)alignment);

        // Padding in code is made of no-ops when it can be, so that falling into it is harmless.
        var nops = _current == _text && offset % 4 == 0 && padding % 4 == 0;
        Place(new FillItem(statement, 0, nops), padding);
    }

    private string? SymbolName(Operand operand)
    {
        if (operand is ExprOperand { Value: SymbolExpr symbol })
        {
            return symbol.Name;
        }

        _diagnostics.Error(
            operand.Span,
            operand is RegisterOperand ? "a register cannot be a symbol" : "expected a symbol name");
        return null;
    }

    private Expr? Number(Operand operand, string? hint = null)
    {
        if (operand is ExprOperand expression)
        {
            return expression.Value;
        }

        _diagnostics.Error(operand.Span, "expected a number", hint);
        return null;
    }

    /// <summary>A number the first pass must already know, because a size depends on it.</summary>
    private long? EarlyNumber(Operand operand, string what)
    {
        if (Number(operand) is not { } expression)
        {
            return null;
        }

        if (TryEvaluateEarly(expression, out var value))
        {
            return value;
        }

        _diagnostics.Error(
            operand.Span,
            $"{what} must be a number that is known at this point",
            "it cannot depend on a label defined later, or on an address");
        return null;
    }
}
