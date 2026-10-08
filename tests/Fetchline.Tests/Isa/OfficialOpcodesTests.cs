using System.Globalization;
using Fetchline.Core.Isa;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Isa;

/// <summary>
/// Holds the instruction table to <c>riscv/riscv-opcodes</c>, the files the RISC-V toolchains are
/// themselves generated from. The copies under <c>tests/vectors/riscv-opcodes</c> were taken from
/// a pinned commit by the Vectors workflow; see <c>tests/vectors/PROVENANCE.md</c>.
/// </summary>
public class OfficialOpcodesTests
{
    // RV32 defines its shifts as the RV64 ones with the sixth shift-amount bit fixed at zero, so
    // the file gives them as pseudo-ops of rv64_i. They are real instructions here.
    private static readonly string[] Rv32Shifts = ["slli", "srli", "srai"];

    private static readonly IReadOnlyList<OfficialOpcode> Official = OpcodesFile.ReadAll();

    [Fact]
    public void EveryMatchAndMaskInTheTableEqualsTheOfficialOne()
    {
        var official = Official
            .Where(o => !o.IsPseudo || Rv32Shifts.Contains(o.Name))
            .ToDictionary(o => o.Name);

        foreach (var row in InstructionSet.All)
        {
            Assert.True(official.TryGetValue(row.Mnemonic, out var theirs), $"{row.Mnemonic} is not in riscv-opcodes");
            Assert.True(
                row.Match == theirs.Match && row.Mask == theirs.Mask,
                $"{row.Mnemonic}: table has match {row.Match:x8} mask {row.Mask:x8}, " +
                $"riscv-opcodes has match {theirs.Match:x8} mask {theirs.Mask:x8}");
        }
    }

    [Fact]
    public void TheTableHasExactlyTheInstructionsOfTheSixFiles()
    {
        var theirs = Official
            .Where(o => !o.IsPseudo || Rv32Shifts.Contains(o.Name))
            .Select(o => o.Name)
            .Order(StringComparer.Ordinal);
        var ours = InstructionSet.All.Select(row => row.Mnemonic).Order(StringComparer.Ordinal);

        Assert.Equal(theirs, ours);
    }

    [Fact]
    public void EveryPseudoOpIsASpecialCaseOfARealInstructionInTheTable()
    {
        // A sanity check on the reader as much as on the files: a pseudo-op only fixes more bits
        // of its base, so it must agree with the base wherever the base is fixed.
        foreach (var pseudo in Official.Where(o => o.IsPseudo))
        {
            Assert.True(InstructionSet.TryGet(pseudo.Base!, out var row), $"{pseudo.Name}: base {pseudo.Base} is unknown");
            Assert.Equal(row.Mask, pseudo.Mask & row.Mask);
            Assert.Equal(row.Match, pseudo.Match & row.Mask);
        }
    }

    [Fact]
    public void EveryCsrNameHasTheOfficialNumber()
    {
        var official = ReadCsv("csrs.csv").Concat(ReadCsv("csrs32.csv")).ToList();
        var numberOf = official.ToDictionary(pair => pair.Name, pair => pair.Number);

        foreach (var (name, number) in Csr.Names)
        {
            Assert.True(numberOf.TryGetValue(name, out var theirs), $"{name} is not in csrs.csv or csrs32.csv");
            Assert.True(number == theirs, $"{name}: table has 0x{number:x3}, riscv-opcodes has 0x{theirs:x3}");
            Assert.True(Csr.TryGetName(number, out var back) && back == name);
            Assert.True(Csr.TryGetNumber(name, out var forth) && forth == number);
        }

        Assert.Equal("0x7c0", Csr.Format(0x7C0));
        Assert.Equal("mstatus", Csr.Format(Csr.Mstatus));
    }

    [Theory]
    [InlineData(Csr.Mstatus, false)]
    [InlineData(Csr.Mcycle, false)]
    [InlineData(Csr.Cycle, true)]
    [InlineData(Csr.Instreth, true)]
    [InlineData(Csr.Mhartid, true)]
    [InlineData(Csr.Mvendorid, true)]
    public void TheTopTwoAddressBitsSayWhetherACsrIsReadOnly(int number, bool readOnly)
    {
        Assert.Equal(readOnly, Csr.IsReadOnly(number));
    }

    private static IEnumerable<(string Name, int Number)> ReadCsv(string file)
    {
        foreach (var line in File.ReadLines(Repo.PathOf("tests", "vectors", "riscv-opcodes", file)))
        {
            // 0x300, "mstatus"
            var parts = line.Split(',', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var number = int.Parse(parts[0].Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            yield return (parts[1].Trim().Trim('"'), number);
        }
    }
}
