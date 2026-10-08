using System.Globalization;
using System.Text.RegularExpressions;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Isa;

/// <summary>
/// Checks the decoder, the disassembler and the assembler against a real toolchain without one
/// being installed. Each official test program was disassembled by GNU objdump on a GitHub runner
/// (<c>objdump -d -M no-aliases</c>); for every instruction word in those listings, this core
/// must name the same instruction with the same operands, and its own text for the word must
/// assemble back to it.
/// </summary>
public partial class ObjdumpCrossCheckTests
{
    private static readonly Lazy<Dictionary<string, int>> OfficialCsrNumbers = new(() =>
        new[] { "csrs.csv", "csrs32.csv" }
            .SelectMany(file => File.ReadLines(Repo.PathOf("tests", "vectors", "riscv-opcodes", file)))
            .Select(line => line.Split(',', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[1].Trim().Trim('"'))
            .ToDictionary(
                group => group.Key,
                group => int.Parse(group.First()[0].Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    public static TheoryData<string> Names() => Vectors.Rows();

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryInstructionInTheListingIsReadAndWrittenTheSameWay(string name)
    {
        var problems = new List<string>();
        var checkedWords = 0;

        foreach (var line in Vectors.Dump(name))
        {
            // "80000000:	0500006f          	jal	zero,80000050 <reset_vector>"
            var match = InstructionLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var address = uint.Parse(match.Groups["address"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var word = uint.Parse(match.Groups["word"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var theirMnemonic = match.Groups["mnemonic"].Value;
            var theirOperands = Annotation().Replace(match.Groups["operands"].Value, string.Empty);

            if (Check(address, word, theirMnemonic, theirOperands) is { } problem)
            {
                problems.Add($"{address:x8}: {word:x8}  {theirMnemonic} {theirOperands}  -- {problem}");
            }

            checkedWords++;
        }

        Assert.True(problems.Count == 0, $"{problems.Count} of {checkedWords} differ:\n" + string.Join('\n', problems.Take(20)));
        Assert.True(checkedWords > 100, $"only {checkedWords} instructions were found in the listing of {name}");
    }

    [Fact]
    public void TheListingsHoldThousandsOfInstructionsAndEveryInstructionOfTheTable()
    {
        var seen = new HashSet<Op>();
        var total = 0;
        foreach (var name in Vectors.Names)
        {
            foreach (var line in Vectors.Dump(name))
            {
                if (InstructionLine().Match(line) is { Success: true } match)
                {
                    total++;
                    seen.Add(Decoder.Decode(uint.Parse(match.Groups["word"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).Op);
                }
            }
        }

        Assert.True(total > 19_000, $"{total} instructions");

        // Nothing in the table goes unchecked by a real toolchain's opinion of it.
        var unseen = InstructionSet.All.Select(row => row.Op).Where(op => !seen.Contains(op));
        Assert.Empty(unseen);
    }

    /// <summary>Compares one line of a listing with this core's reading of the word. Null means they agree.</summary>
    private static string? Check(uint address, uint word, string theirMnemonic, string theirOperands)
    {
        var decoded = Decoder.Decode(word);
        if (!decoded.IsLegal)
        {
            // Supervisor, floating-point and reserved encodings are in these tests on purpose.
            // They are fine as long as objdump does not think they belong to this instruction set.
            return InstructionSet.TryGet(theirMnemonic, out _) ? "decoded as illegal" : null;
        }

        var mine = Disassembler.Disassemble(word, address, DisassemblyOptions.Canonical);
        if (theirMnemonic == "unimp")
        {
            // binutils gives this one encoding of csrrw a name of its own even without aliases.
            return word == Disassembler.Unimplemented ? Reassemble(address, word, "unimp") : "unimp is another word here";
        }

        if (mine.Mnemonic != theirMnemonic)
        {
            return $"disassembled as '{mine}'";
        }

        var syntax = InstructionSet.Get(decoded.Op).Syntax;
        var (ours, theirs) = (Tokens(mine.Operands), Tokens(theirOperands));
        if (ours.Length != theirs.Length)
        {
            return $"disassembled as '{mine}'";
        }

        for (var i = 0; i < ours.Length; i++)
        {
            var same = ours[i] == theirs[i] || (syntax, i) switch
            {
                // objdump prints a target as bare hex; a CSR it does not know as a number.
                (Syntax.Rs1Rs2Target, 2) or (Syntax.RdTarget, 1) => Number(ours[i]) == Hex(theirs[i]),
                (Syntax.RdCsrRs1 or Syntax.RdCsrZimm, 1) => CsrNumber(ours[i]) == CsrNumber(theirs[i]),
                _ => Number(ours[i]) is { } value && value == Number(theirs[i]),
            };
            if (!same)
            {
                return $"disassembled as '{mine}'";
            }
        }

        return Reassemble(address, word, mine.ToString());
    }

    private static string? Reassemble(uint address, uint word, string text)
    {
        var result = Assembler.Assemble(text, new AssemblerOptions { TextBase = address, DataBase = address ^ 0x8000_0000 });
        if (!result.Success)
        {
            return $"'{text}' does not assemble: {result.Diagnostics[0].Message}";
        }

        return result.Program!.Words() is [var back] && back == word ? null : $"'{text}' assembles to another word";
    }

    private static string[] Tokens(string operands) =>
        operands.Split([',', '(', ')', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static long? Number(string token)
    {
        var negative = token.StartsWith('-');
        var digits = negative ? token[1..] : token;
        long value;
        if (digits.StartsWith("0x", StringComparison.Ordinal))
        {
            if (!long.TryParse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return null;
            }
        }
        else if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return negative ? -value : value;
    }

    private static long? Hex(string token) =>
        long.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? CsrNumber(string token)
    {
        if (Csr.TryGetNumber(token, out var known))
        {
            return known;
        }

        return OfficialCsrNumbers.Value.TryGetValue(token, out var official) ? official : Number(token);
    }

    // Only whole 32-bit words: a few tests hold 16-bit encodings, which this core does not have.
    [GeneratedRegex(@"^\s*(?<address>[0-9a-f]+):\t(?<word>[0-9a-f]{8})\s+\t(?<mnemonic>\S+)(\t(?<operands>.*))?$")]
    private static partial Regex InstructionLine();

    // " <reset_vector+0x80>" after a target, and " # 80001000 <tohost>" after an address.
    [GeneratedRegex(@"\s*(<[^>]*>|#.*)")]
    private static partial Regex Annotation();
}
