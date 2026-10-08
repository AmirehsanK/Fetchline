using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Asm;

/// <summary>
/// The disassembler writes what the assembler reads. For any word at any address, the text the
/// disassembler gives, in either form, assembles at that address back to the very same word.
/// This is what lets the playground show an instruction as text and trust it.
/// </summary>
public class RoundTripTests
{
    [Fact]
    public void AnyWordReassemblesFromItsDisassemblyInBothForms()
    {
        var random = new SeededRandom(0xF37C_2A01);
        var legal = 0;
        var aliased = 0;

        for (var i = 0; i < 60_000; i++)
        {
            // Three words in four are given an opcode from the table, or nearly all would be illegal.
            var word = random.NextUInt32();
            if (random.Chance(75))
            {
                word = (word & ~0x7Fu) | (random.Pick(InstructionSet.All).Match & 0x7F);
            }

            // Aliases only appear when fields are zero, which random bits rarely are; clear some.
            if (random.Chance(40))
            {
                word &= random.Next(0, 3) switch
                {
                    0 => ~(31u << 7),              // rd = x0
                    1 => ~(31u << 15),             // rs1 = x0
                    2 => ~(31u << 20),             // rs2 = x0, or a small immediate
                    _ => ~0xFFF0_0000u,            // immediate = 0
                };
            }

            var pc = random.NextUInt32() & ~3u;
            var canonical = Disassembler.Format(word, pc, DisassemblyOptions.Canonical);
            var withAliases = Disassembler.Format(word, pc);

            legal += Decoder.Decode(word).IsLegal ? 1 : 0;
            aliased += canonical != withAliases ? 1 : 0;

            Check(word, pc, canonical, random);
            if (withAliases != canonical)
            {
                Check(word, pc, withAliases, random);
            }
        }

        // Guard the test itself: it must really have exercised instructions and aliases.
        Assert.True(legal > 20_000, $"only {legal} legal words were tried");
        Assert.True(aliased > 1_500, $"only {aliased} words had an alias form");
    }

    [Fact]
    public void EveryAliasTheDisassemblerUsesIsExercised()
    {
        // One instance of each alias, so that a new alias without a matching assembler form
        // cannot hide behind the odds of the random test.
        string[] expected =
        [
            "nop", "li", "mv", "not", "neg", "seqz", "snez", "sltz", "sgtz",
            "beqz", "bnez", "bgez", "blez", "bltz", "bgtz",
            "j", "jal", "ret", "jr", "jalr",
            "csrr", "csrw", "csrs", "csrc", "csrwi", "csrsi", "csrci", "fence", "unimp",
        ];
        uint[] words =
        [
            0x00000013, 0x00500513, 0x00058513, 0xFFF54513, 0x40B00533, 0x00153513, 0x00B03533, 0x0005A533, 0x00B02533,
            0x00050463, 0x00051463, 0x00055463, 0x00A05463, 0x00054463, 0x00A04463,
            0x0000006F, 0x008000EF, 0x00008067, 0x000F0067, 0x000500E7,
            0x34202F73, 0x30529073, 0x3002A073, 0x3002B073, 0x74445073, 0x30046073, 0x30047073, 0x0FF0000F, 0xC0001073,
        ];

        var random = new SeededRandom(1);
        for (var i = 0; i < words.Length; i++)
        {
            var line = Disassembler.Disassemble(words[i], 0x400);
            Assert.Equal(expected[i], line.Mnemonic);
            Check(words[i], 0x400, line.ToString(), random);
        }
    }

    private static void Check(uint word, uint pc, string text, SeededRandom random)
    {
        var options = new AssemblerOptions { TextBase = pc, DataBase = pc ^ 0x8000_0000 };
        var result = Assembler.Assemble(text, options);

        if (!result.Success)
        {
            Assert.Fail($"seed {random.Seed:X}: {word:x8} at {pc:x8} is '{text}', which does not assemble:\n"
                + result.RenderDiagnostics());
        }

        var words = result.Program!.Words();
        if (words is not [var back] || back != word)
        {
            Assert.Fail($"seed {random.Seed:X}: {word:x8} at {pc:x8} is '{text}', which assembles to "
                + string.Join(' ', words.Select(w => w.ToString("x8"))));
        }
    }
}
