using Fetchline.Core.Asm;
using Fetchline.Core.Isa;

namespace Fetchline.Viz;

/// <summary>
/// How instructions and registers are written in a diagram. Where an instruction came from one
/// line of source, it is shown as that line was written: <c>bnez t0, loop</c>, not the address the
/// label stood for, and <c>x4</c> if the program says <c>x4</c>. A pseudo-instruction that became
/// several machine instructions is shown as those, because each has a row of its own, and so is
/// everything in a program that came from an ELF file.
/// </summary>
public sealed class InstructionLabels
{
    private readonly Program _program;
    private readonly DisassemblyOptions _options;

    public InstructionLabels(Program program)
    {
        _program = program;
        Registers = DetectStyle(program.Source);
        _options = new DisassemblyOptions { Registers = Registers, SymbolAt = program.LabelAt };
    }

    /// <summary>Whether the program writes its registers as <c>a0</c> or as <c>x10</c>.</summary>
    public RegisterStyle Registers { get; }

    /// <summary>The instruction at an address, as text.</summary>
    public DisassembledLine Describe(uint pc, uint raw)
    {
        if (_program.Source is { } source && _program.SourceMap.TryGetByAddress(pc, out var entry) && entry.PartCount == 1)
        {
            return Tidy(source.Substring(entry.Start, entry.Length));
        }

        return Disassembler.Disassemble(raw, pc, _options);
    }

    /// <summary>The name of a register in the program's own style.</summary>
    public string Register(int index) => Core.Isa.Registers.Name(index, Registers);

    /// <summary>An address as the program names it: its label when it has one, otherwise in hex.</summary>
    public string Address(uint address) =>
        _program.LabelAt(address) ?? "0x" + address.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A statement as written, with its spacing made regular.</summary>
    private static DisassembledLine Tidy(string statement)
    {
        var text = statement.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        if (space < 0)
        {
            return new DisassembledLine(text.ToLowerInvariant(), string.Empty);
        }

        var operands = text[space..].Split(',')
            .Select(operand => string.Join(' ', operand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return new DisassembledLine(text[..space].ToLowerInvariant(), string.Join(", ", operands));
    }

    private static RegisterStyle DetectStyle(string? source)
    {
        if (source is null)
        {
            return RegisterStyle.Abi;
        }

        int numeric = 0, abi = 0;
        foreach (var token in Lexer.Tokenize(source))
        {
            if (token.Kind == TokenKind.Identifier && Core.Isa.Registers.TryParse(token.Text, out var index))
            {
                if (token.Text == Core.Isa.Registers.Name(index, RegisterStyle.Numeric))
                {
                    numeric++;
                }
                else
                {
                    abi++;
                }
            }
        }

        return numeric > abi ? RegisterStyle.Numeric : RegisterStyle.Abi;
    }
}
