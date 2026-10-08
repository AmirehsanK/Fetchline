using System.Globalization;

namespace Fetchline.Tests.Support;

/// <summary>One instruction as <c>riscv/riscv-opcodes</c> defines it.</summary>
/// <param name="Base">For a pseudo-op, the real instruction it is a special case of.</param>
/// <param name="Fields">The names of the variable fields, in the order the file gives them.</param>
internal sealed record OfficialOpcode(string Name, uint Match, uint Mask, string? Base, IReadOnlyList<string> Fields)
{
    public bool IsPseudo => Base is not null;
}

/// <summary>
/// Reads the official opcode files under <c>tests/vectors/riscv-opcodes</c>. A line names an
/// instruction, then lists its variable fields by name and its fixed bits as <c>hi..lo=value</c>:
/// <code>lui rd imm20 6..2=0x0D 1..0=3</code>
/// A <c>$pseudo_op</c> line defines a special case of another instruction with more bits fixed.
/// </summary>
internal static class OpcodesFile
{
    /// <summary>The six files that between them cover this core's instruction set.</summary>
    public static readonly string[] Files = ["rv_i", "rv32_i", "rv_m", "rv_zicsr", "rv_zifencei", "rv_system"];

    public static IReadOnlyList<OfficialOpcode> ReadAll() =>
        [.. Files.SelectMany(file => Read(Repo.PathOf("tests", "vectors", "riscv-opcodes", file)))];

    public static IEnumerable<OfficialOpcode> Read(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0 || line.StartsWith("$import", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string? baseName = null;
            var first = 0;
            if (tokens[0] == "$pseudo_op")
            {
                baseName = tokens[1].Split("::")[1];
                first = 2;
            }

            uint match = 0, mask = 0;
            var fields = new List<string>();
            foreach (var token in tokens.Skip(first + 1))
            {
                var equals = token.IndexOf('=');
                if (equals < 0)
                {
                    fields.Add(token);
                    continue;
                }

                var range = token[..equals].Split("..");
                var high = int.Parse(range[0], CultureInfo.InvariantCulture);
                var low = range.Length == 2 ? int.Parse(range[1], CultureInfo.InvariantCulture) : high;
                var value = ParseNumber(token[(equals + 1)..]);
                var width = high - low + 1;
                var fieldMask = (width == 32 ? uint.MaxValue : (1u << width) - 1) << low;

                if ((mask & fieldMask) != 0 || (value << low & ~fieldMask) != 0)
                {
                    throw new InvalidDataException($"{path}: '{line}' fixes a bit twice or overflows a field");
                }

                mask |= fieldMask;
                match |= value << low;
            }

            yield return new OfficialOpcode(tokens[first], match, mask, baseName, fields);
        }
    }

    private static uint ParseNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
}
