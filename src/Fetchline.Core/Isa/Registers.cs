namespace Fetchline.Core.Isa;

/// <summary>How a register is written out: <c>a0</c> or <c>x10</c>.</summary>
public enum RegisterStyle : byte
{
    /// <summary>The calling-convention names: <c>zero</c>, <c>ra</c>, <c>sp</c>, <c>a0</c>…</summary>
    Abi,

    /// <summary>The architectural names: <c>x0</c> to <c>x31</c>.</summary>
    Numeric,
}

/// <summary>The 32 integer registers and the names assembly sources use for them.</summary>
public static class Registers
{
    /// <summary>How many integer registers RV32I has.</summary>
    public const int Count = 32;

    private static readonly string[] Abi =
    [
        "zero", "ra", "sp", "gp", "tp", "t0", "t1", "t2",
        "s0", "s1", "a0", "a1", "a2", "a3", "a4", "a5",
        "a6", "a7", "s2", "s3", "s4", "s5", "s6", "s7",
        "s8", "s9", "s10", "s11", "t3", "t4", "t5", "t6",
    ];

    private static readonly string[] Numeric = [.. Enumerable.Range(0, Count).Select(i => "x" + i)];

    /// <summary>Every spelling the assembler accepts, for "did you mean" suggestions.</summary>
    public static IReadOnlyList<string> AllNames { get; } = [.. Abi, "fp", .. Numeric];

    /// <summary>The name of register <paramref name="index"/> in the given style.</summary>
    public static string Name(int index, RegisterStyle style = RegisterStyle.Abi) =>
        style == RegisterStyle.Abi ? Abi[index] : Numeric[index];

    /// <summary>
    /// Reads a register name: <c>x0</c> to <c>x31</c>, an ABI name, or <c>fp</c> (the other name
    /// of <c>s0</c>). Names are lower case, as in GNU <c>as</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out int index)
    {
        index = -1;
        if (text.Length is < 2 or > 4)
        {
            return false;
        }

        if (text[0] == 'x' && IsDecimal(text[1..], out var number))
        {
            index = number;
            return number < Count;
        }

        if (text is "fp")
        {
            index = 8;
            return true;
        }

        for (var i = 0; i < Abi.Length; i++)
        {
            if (text.SequenceEqual(Abi[i]))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    // "x07" and "x+1" are not register names, so this accepts canonical decimal only.
    private static bool IsDecimal(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        if (digits.Length is 0 or > 2 || (digits.Length == 2 && digits[0] == '0'))
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }
}
