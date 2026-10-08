using System.Globalization;
using System.Text;
using Fetchline.Core.Isa;

namespace Fetchline.Core.Asm;

/// <summary>
/// A program's code as text: one line for each word of the text segment, with its address, the
/// word and its disassembly. Where the source said something else (a pseudo-instruction, a label
/// where the listing has an address) the source is shown beside it.
/// </summary>
public static class Listing
{
    private const int SourceColumn = 46;

    public static string Write(Program program, DisassemblyOptions? options = null)
    {
        var text = program.Text;
        if (text is null || text.Size < 4)
        {
            return string.Empty;
        }

        options ??= DisassemblyOptions.Default;
        options = options with { SymbolAt = options.SymbolAt ?? program.LabelAt };

        // A program from an ELF file has no source map, and everything in its text is taken to
        // be code. An assembled one knows which words are instructions and which are data.
        var hasMap = program.SourceMap.Entries.Count > 0;
        var listing = new StringBuilder();

        for (ulong at = text.Address; at + 4 <= (ulong)text.Address + text.Size; at += 4)
        {
            var address = (uint)at;
            if (program.LabelAt(address) is { } label)
            {
                if (listing.Length > 0)
                {
                    listing.Append('\n');
                }

                listing.Append(CultureInfo.InvariantCulture, $"{address:x8} <{label}>:\n");
            }

            program.TryReadWord(address, out var word);
            var entry = default(SourceMapEntry);
            var isCode = !hasMap || program.SourceMap.TryGetByAddress(address, out entry);
            var line = isCode
                ? Disassembler.Disassemble(word, address, options)
                : new DisassembledLine(".word", "0x" + word.ToString("x8", CultureInfo.InvariantCulture));

            var row = new StringBuilder();
            row.Append(CultureInfo.InvariantCulture, $"{address:x8}:  {word:x8}  ");
            row.Append(line.Operands.Length == 0 ? line.Mnemonic : $"{line.Mnemonic,-7} {line.Operands}");

            if (isCode && hasMap && entry.Part == 0 && program.Source is { } source)
            {
                var written = source.Substring(entry.Start, entry.Length);
                if (Normalize(written) != Normalize(line.ToString()))
                {
                    row.Append(' ', Math.Max(2, SourceColumn - row.Length));
                    row.Append("# ").Append(Normalize(written));
                }
            }

            listing.Append(row).Append('\n');
        }

        return listing.ToString();
    }

    private static string Normalize(string statement) =>
        string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
