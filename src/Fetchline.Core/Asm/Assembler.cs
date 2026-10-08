using System.Buffers.Binary;
using System.Globalization;

namespace Fetchline.Core.Asm;

/// <summary>Where the assembler puts the sections.</summary>
public sealed record AssemblerOptions
{
    /// <summary>The address of <c>.text</c>. The default is what Ripes and Venus use.</summary>
    public uint TextBase { get; init; }

    /// <summary>The address of <c>.data</c>; <c>.rodata</c> and <c>.bss</c> follow it.</summary>
    public uint DataBase { get; init; } = 0x1000_0000;
}

/// <summary>What assembling a source gave: a program, or the reasons there is none.</summary>
public sealed class AssemblyResult(Program? program, IReadOnlyList<Diagnostic> diagnostics, string source)
{
    /// <summary>The program, or null when any diagnostic is an error.</summary>
    public Program? Program { get; } = program;

    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    public string Source { get; } = source;

    public bool Success => Program is not null;

    /// <summary>Every diagnostic as text, each with its source line and a caret.</summary>
    public string RenderDiagnostics(string? fileName = null) =>
        string.Concat(Diagnostics.Select(diagnostic => diagnostic.Render(Source, fileName)));
}

/// <summary>
/// A two-pass assembler for a GNU <c>as</c> compatible subset. The first pass fixes the size of
/// everything and so the offset of every label; the sections are then given addresses; the second
/// pass evaluates every expression and writes the bytes.
/// </summary>
public static class Assembler
{
    /// <summary>Every directive the assembler accepts, including the ones it only skips.</summary>
    public static IReadOnlyList<string> Directives { get; } =
    [
        ".text", ".data", ".rodata", ".bss", ".section", ".globl", ".global",
        ".align", ".p2align", ".balign",
        ".byte", ".half", ".2byte", ".short", ".word", ".4byte", ".long", ".int", ".dword", ".8byte", ".quad",
        ".ascii", ".asciz", ".string", ".zero", ".space", ".skip", ".equ", ".set",
        ".option", ".type", ".size", ".file", ".attribute", ".ident", ".local", ".weak",
    ];

    /// <summary>Every mnemonic the assembler accepts: the real instructions and the pseudo-instructions.</summary>
    public static IReadOnlyCollection<string> Mnemonics => Assembly.Mnemonics;

    public static AssemblyResult Assemble(string source, AssemblerOptions? options = null) =>
        new Assembly(source, options ?? new AssemblerOptions()).Run();
}

/// <summary>One run of the assembler over one source.</summary>
internal sealed partial class Assembly : IExprScope
{
    // Sizes are bounded so that ".zero 0x7fffffff" is a diagnostic and not two gigabytes of zeros.
    private const uint MaxSectionSize = 16 * 1024 * 1024;
    private const uint MaxBssSize = 64 * 1024 * 1024;

    private readonly string _source;
    private readonly AssemblerOptions _options;
    private readonly DiagnosticBag _diagnostics = new();

    private readonly Section _text = new(0, ".text", SegmentFlags.Read | SegmentFlags.Execute);
    private readonly Section _data = new(1, ".data", SegmentFlags.Read | SegmentFlags.Write);
    private readonly Section _rodata = new(2, ".rodata", SegmentFlags.Read);
    private readonly Section _bss = new(3, ".bss", SegmentFlags.Read | SegmentFlags.Write);
    private readonly Section[] _sections;
    private readonly List<Item> _items = [];
    private readonly Dictionary<uint, long> _pcrel = [];

    // Where the statement being processed is: what "." and local label references are relative to.
    private Section _current;
    private Section _hereSection;
    private uint _hereOffset;
    private int _statementIndex;
    private bool _final;
    private bool _quiet;

    public Assembly(string source, AssemblerOptions options)
    {
        _source = source;
        _options = options;
        _sections = [_text, _data, _rodata, _bss];
        _current = _hereSection = _text;
    }

    public AssemblyResult Run()
    {
        var statements = Parser.Parse(_source, _diagnostics);
        FirstPass(statements);
        Layout();
        SecondPass();

        // The parser, the two passes and the layout each find their own mistakes; the reader wants
        // them in the order of the source.
        var diagnostics = _diagnostics.Items.OrderBy(d => d.Span.Line).ThenBy(d => d.Span.Column).ToList();
        return new AssemblyResult(_diagnostics.HasErrors ? null : BuildProgram(), diagnostics, _source);
    }

    // ---- First pass: sizes, and so the offset of every label ----------------------------------

    private void FirstPass(List<Statement> statements)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            _statementIndex = index;
            _hereSection = _current;
            _hereOffset = _current.Size;

            // Labels are defined even on a broken line, so that using one is not a second error.
            foreach (var label in statement.Labels)
            {
                DefineLabel(label);
            }

            if (statement.IsBroken)
            {
                continue;
            }

            switch (statement.Kind)
            {
                case StatementKind.Assignment:
                    DefineConstant(statement.Name, statement.NameSpan, ((ExprOperand)statement.Operands[0]).Value);
                    break;
                case StatementKind.Directive:
                    Directive(statement);
                    break;
                case StatementKind.Instruction:
                    Instruction(statement);
                    break;
            }
        }
    }

    /// <summary>Places an item at the current location and advances past it.</summary>
    private void Place(Item item, uint size)
    {
        item.Section = _current;
        item.Offset = _current.Size;
        item.StatementIndex = _statementIndex;
        item.Size = size;
        _items.Add(item);

        var limit = _current == _bss ? MaxBssSize : MaxSectionSize;
        if (size > limit || _current.Size + size > limit)
        {
            if (!_current.TooLarge)
            {
                _diagnostics.Error(
                    item.Statement.Span,
                    $"{_current.Name} would be larger than {limit / (1024 * 1024)} MB");
                _current.TooLarge = true;
            }

            item.Size = 0;
            return;
        }

        _current.Size += size;
    }

    // ---- Layout: every section gets an address -------------------------------------------------

    private void Layout()
    {
        _text.Base = _options.TextBase;
        _data.Base = _options.DataBase;
        _rodata.Base = AlignUp(_data.Base + _data.Size, _rodata.Alignment);
        _bss.Base = AlignUp(_rodata.Base + _rodata.Size, _bss.Alignment);

        if (_text.Base < _data.Base && (ulong)_text.Base + _text.Size > _data.Base)
        {
            _diagnostics.Error(default, $".text is {_text.Size} bytes and runs into .data at 0x{_data.Base:x8}");
        }

        foreach (var section in _sections)
        {
            if (section != _bss)
            {
                section.Bytes = new byte[section.Size];
            }
        }
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    // ---- Second pass: every expression has a value, every byte is written ---------------------

    private void SecondPass()
    {
        _final = true;
        CollectPcrelHi();

        foreach (var item in _items)
        {
            EnterItem(item);
            switch (item)
            {
                case DataItem data:
                    EmitData(data);
                    break;
                case BytesItem bytes:
                    Write(bytes, 0, bytes.Bytes);
                    break;
                case FillItem fill:
                    EmitFill(fill);
                    break;
                case InstructionItem instruction:
                    instruction.Form.Emit(new EmitContext(this, instruction));
                    break;
            }
        }

        ResolveConstants();
    }

    private void EnterItem(Item item)
    {
        _hereSection = item.Section;
        _hereOffset = item.Offset;
        _statementIndex = item.StatementIndex;
    }

    private void EmitData(DataItem item)
    {
        Span<byte> buffer = stackalloc byte[8];
        for (var i = 0; i < item.Statement.Operands.Count; i++)
        {
            var operand = (ExprOperand)item.Statement.Operands[i];
            if (EvaluateFinal(operand.Value) is not { } value)
            {
                continue;
            }

            var bits = item.Width * 8;
            if (bits < 64 && (value < -(1L << (bits - 1)) || value > (1L << bits) - 1))
            {
                _diagnostics.Error(operand.Span, $"{value} does not fit in {item.Width} byte{(item.Width == 1 ? "" : "s")}");
                continue;
            }

            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            Write(item, i * item.Width, buffer[..item.Width], operand.Span);
        }
    }

    private void EmitFill(FillItem item)
    {
        if (item.Section == _bss || item.Size == 0)
        {
            if (item.Value != 0 && item.Section == _bss)
            {
                _diagnostics.Error(item.Statement.Span, ".bss can only be filled with zeros");
            }

            return;
        }

        var target = item.Section.Bytes.AsSpan((int)item.Offset, (int)item.Size);
        if (item.Nops)
        {
            // 0x00000013 is "addi x0, x0, 0": padding that can be executed.
            for (var offset = 0; offset < target.Length; offset += 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], 0x0000_0013);
            }
        }
        else
        {
            target.Fill(item.Value);
        }
    }

    private void Write(Item item, int offsetInItem, ReadOnlySpan<byte> bytes, SourceSpan? at = null)
    {
        if (offsetInItem + bytes.Length > item.Size)
        {
            return;   // The item was dropped for making its section too large.
        }

        if (item.Section == _bss)
        {
            if (bytes.ContainsAnyExcept((byte)0))
            {
                _diagnostics.Error(
                    at ?? item.Statement.Span,
                    ".bss holds no data, only space",
                    "put initialised data in .data, or reserve space with .zero");
            }

            return;
        }

        bytes.CopyTo(item.Section.Bytes.AsSpan((int)item.Offset + offsetInItem));
    }

    // ---- The result ----------------------------------------------------------------------------

    private Program BuildProgram()
    {
        var segments = _sections
            .Where(section => section == _text || section.Size > 0)
            .Select(section => new Segment(
                section.Name.TrimStart('.'), section.Base, section.Bytes, section.Size, section.Flags));

        var symbols = _symbols.Values.Select(entry => new Symbol(
            entry.Name,
            (uint)entry.FinalValue,
            entry.IsLabel ? SymbolKind.Label : SymbolKind.Constant,
            _globals.Contains(entry.Name),
            entry.IsLabel ? entry.Section!.Name : null));

        var map = _items.OfType<InstructionItem>().SelectMany(item =>
            Enumerable.Range(0, item.Count).Select(part => new SourceMapEntry(
                item.Section.Base + item.Offset + (uint)(4 * part),
                item.Statement.Span.Line,
                item.Statement.Span.Column,
                item.Statement.Span.Start,
                item.Statement.Span.Length,
                part,
                item.Count)));

        var entry = _symbols.TryGetValue("_start", out var start) && start.IsLabel ? (uint)start.FinalValue : _text.Base;
        return new Program(segments, symbols, entry, new SourceMap(map), _source);
    }

    private static string Hex(long value) => "0x" + ((uint)value).ToString("x8", CultureInfo.InvariantCulture);

    // ---- The pieces a source is broken into ----------------------------------------------------

    private sealed class Section(int index, string name, SegmentFlags flags)
    {
        public int Index { get; } = index;

        public string Name { get; } = name;

        public SegmentFlags Flags { get; } = flags;

        /// <summary>The location counter during the first pass; the section's size after it.</summary>
        public uint Size { get; set; }

        public uint Base { get; set; }

        public byte[] Bytes { get; set; } = [];

        /// <summary>The strictest alignment asked for inside the section; its address honours it.</summary>
        public uint Alignment { get; set; } = 16;

        public bool TooLarge { get; set; }
    }

    private abstract class Item(Statement statement)
    {
        public Statement Statement { get; } = statement;

        public Section Section { get; set; } = null!;

        public uint Offset { get; set; }

        public uint Size { get; set; }

        public int StatementIndex { get; set; }
    }

    /// <summary>Numbers of one width, one per operand of the statement.</summary>
    private sealed class DataItem(Statement statement, int width) : Item(statement)
    {
        public int Width { get; } = width;
    }

    private sealed class BytesItem(Statement statement, byte[] bytes) : Item(statement)
    {
        public byte[] Bytes { get; } = bytes;
    }

    /// <summary>Space: from <c>.zero</c>, or the padding of an alignment.</summary>
    private sealed class FillItem(Statement statement, byte value, bool nops) : Item(statement)
    {
        public byte Value { get; } = value;

        public bool Nops { get; } = nops;
    }

    private sealed class InstructionItem(Statement statement, Form form, int count) : Item(statement)
    {
        public Form Form { get; } = form;

        /// <summary>How many machine instructions the statement becomes; fixed in the first pass.</summary>
        public int Count { get; } = count;
    }
}
