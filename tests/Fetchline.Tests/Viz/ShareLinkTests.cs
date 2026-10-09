using System.IO.Compression;
using System.Text;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Support;
using Fetchline.Viz;

namespace Fetchline.Tests.Viz;

/// <summary>
/// A share link: what is on screen as a piece of text for a web address. Anyone can write one,
/// so reading one is held to never throwing, never trusting, and never accepting what it could
/// not have written itself.
/// </summary>
public class ShareLinkTests
{
    private static readonly Shared Simple = new("li a0, 5\n", PipelineConfig.Default, 3);

    private static Shared Decoded(string link)
    {
        Assert.True(ShareLink.TryDecode(link, out var shared, out var problem), problem.ToString());
        Assert.Equal(LinkProblem.None, problem);
        return shared;
    }

    private static LinkProblem Refused(string? link)
    {
        Assert.False(ShareLink.TryDecode(link, out var shared, out var problem));
        Assert.Null(shared);
        Assert.NotEqual(LinkProblem.None, problem);
        return problem;
    }

    /// <summary>A link made by hand from a header and a source, to say things the encoder never would.</summary>
    private static string Forge(string payload) => Forge(Encoding.UTF8.GetBytes(payload));

    private static string Forge(byte[] payload)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return "1." + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The header the encoder writes for a source, with one line changed or added.</summary>
    private static string Header(string source, string? change = null, string? drop = null)
    {
        var plain = ShareLink.Encode(new Shared(source, PipelineConfig.Default, 0));
        var lines = Inflate(plain).Split("\n\n")[0].Split('\n').Where(line => drop is null || !line.StartsWith(drop + "=")).ToList();
        if (change is not null)
        {
            var name = change[..change.IndexOf('=')];
            var index = lines.FindIndex(line => line.StartsWith(name + "=", StringComparison.Ordinal));
            if (index >= 0)
            {
                lines[index] = change;
            }
            else
            {
                lines.Add(change);
            }
        }

        return string.Join('\n', lines) + "\n\n" + source;
    }

    private static string Inflate(string link)
    {
        var packed = link[2..].Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(packed.PadRight((packed.Length + 3) / 4 * 4, '='));
        using var inflate = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void WhatGoesIntoALinkComesOutOfIt()
    {
        foreach (var example in ExampleCatalog.All)
        {
            foreach (var config in Configurations.Correct.Where((_, index) => index % 9 == 0)
                .Append(new PipelineConfig { Hazards = HazardHandling.Off, BtbEntries = 16, MulDivCycles = 3 }))
            {
                foreach (var cycle in new[] { 0ul, 1ul, 68ul, ShareLink.MostCycle })
                {
                    var shared = new Shared(example.Source, config, cycle);
                    Assert.Equal(shared, Decoded(ShareLink.Encode(shared)));
                }
            }
        }
    }

    [Fact]
    public void ALinkIsMadeOfNothingAWebAddressObjectsTo()
    {
        var link = ShareLink.Encode(new Shared(ExampleCatalog.Find("primes.s")!.Source, PipelineConfig.Default, 12_345));

        Assert.StartsWith("1.", link);
        Assert.Matches("^1\\.[A-Za-z0-9_-]+$", link);

        // The longest example is two kilobytes of source and well under one of link.
        Assert.InRange(link.Length, 400, 1400);

        // As the fragment of an address it begins with a hash, and is read the same.
        Assert.Equal(Decoded(link), Decoded("#" + link));
    }

    [Fact]
    public void TheSourceIsKeptExactlyWhateverIsInIt()
    {
        foreach (var source in new[]
        {
            string.Empty,
            "\n",
            "# سلام: یک برنامهٔ کوچک\n\tli a0, 1\r\n",
            "li a0, '\\n'  # \"quoted\" & <tagged> = 100%\n\n\n",
            "hazards=off\n\ncycle=9\n",                       // looks like a header; it is only source
            new string('x', 5000) + (char)0 + (char)1 + (char)0xFFFD,
        })
        {
            Assert.Equal(source, Decoded(ShareLink.Encode(Simple with { Source = source })).Source);
        }
    }

    [Fact]
    public void WhatALinkHoldsIsPlainEnoughToRead()
    {
        var config = new PipelineConfig { Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit, BtbEntries = 256, MulDivCycles = 3 };

        Assert.Equal(
            "hazards=stall\nbranch=id\npredictor=2-bit\nbtb=256\nmuldiv=3\nicache=off\ndcache=off\ncycle=42\ncheck=af63dc4c8601ec8c\n\na",
            Inflate(ShareLink.Encode(new Shared("a", config, 42))));

        var cached = config with { InstructionCache = new CacheConfig(4, 1, 16, 10), DataCache = new CacheConfig(8, 4, 32, 3) };
        Assert.Contains("\nicache=4x1x16:10\ndcache=8x4x32:3\n", Inflate(ShareLink.Encode(new Shared("a", cached, 42))));
        Assert.Equal(cached, Decoded(ShareLink.Encode(new Shared("a", cached, 42))).Config);

        // A link made before a link said anything about caches has neither line. It still
        // opens, and means a pipeline with neither cache: this is what one held.
        var before = Decoded(Forge("hazards=stall\nbranch=id\npredictor=2-bit\nbtb=256\nmuldiv=3\ncycle=42\ncheck=af63dc4c8601ec8c\n\na"));
        Assert.Equal((config, 42ul, "a"), (before.Config, before.Cycle, before.Source));

        // A cache that is not one, or that could not be built, is a setting there is not.
        foreach (var bad in new[] { "icache=on", "dcache=3x1x8:1", "dcache=4x1x8:0", "icache=4x1x8:01x", "dcache=" })
        {
            Assert.Equal(LinkProblem.UnknownSettings, Refused(Forge(Header("a", change: bad))));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("1.")]
    [InlineData("1")]
    [InlineData("2.AAAA")]                      // a later kind of link
    [InlineData("01.AAAA")]
    [InlineData("AAAA")]
    [InlineData("1.AAAA")]                      // base64, but not deflate
    [InlineData("1.A")]                         // not a length base64 can have
    [InlineData("1.AA+A")]                      // the ordinary alphabet, not the one for addresses
    [InlineData("1.AA/A")]
    [InlineData("1.AAA=")]
    [InlineData("1.AA A")]
    [InlineData("1.AA\nA")]
    [InlineData("1.éééé")]
    public void WhatIsNotALinkIsRefusedWithAReason(string? text)
    {
        Refused(text);
    }

    [Fact]
    public void ALinkThatSaysSomethingTheEncoderNeverWouldIsRefused()
    {
        const string source = "li a0, 5\n";

        // The forger's tools are sound: an untouched header is accepted.
        Assert.Equal(source, Decoded(Forge(Header(source))).Source);

        foreach (var change in new[]
        {
            "hazards=sometimes", "hazards=Forwarding", "hazards=", "branch=wb", "predictor=3-bit",
            "btb=100", "btb=0", "btb=131072", "btb=-64", "btb=064", "btb=64 ", "btb=sixteen", "btb=6.4e1",
            "muldiv=0", "muldiv=65", "muldiv=01", "cycle=250001", "cycle=-1", "cycle=18446744073709551616", "cycle=",
            "check=0000000000000000", "check=",
            "colour=green",                              // a setting there is not
        })
        {
            Assert.Equal(
                change.StartsWith("check", StringComparison.Ordinal) ? LinkProblem.CutShort : LinkProblem.UnknownSettings,
                Refused(Forge(Header(source, change))));
        }

        // A setting given twice, a line that is no setting, no header at all, no check.
        var header = Header(source);
        Refused(Forge(header.Replace("btb=64\n", "btb=64\nbtb=64\n")));
        Refused(Forge(header.Replace("btb=64\n", "btb\n")));
        Refused(Forge(header.Replace("btb=64\n", "=64\n")));
        Refused(Forge(source));
        Refused(Forge("\n\n" + source));
        Assert.Equal(LinkProblem.CutShort, Refused(Forge(Header(source, drop: "check"))));

        // Settings left out take their defaults, as long as the check is there.
        var bare = Header(source, drop: "btb");
        Assert.Equal(new Shared(source, PipelineConfig.Default, 0), Decoded(Forge(bare)));
    }

    [Fact]
    public void ASourceThatWasChangedOrCutShortOnTheWayIsRefused()
    {
        var header = Header("li a0, 5\nli a1, 6\n");

        Assert.Equal(LinkProblem.CutShort, Refused(Forge(header[..^1])));
        Assert.Equal(LinkProblem.CutShort, Refused(Forge(header + "nop\n")));
        Assert.Equal(LinkProblem.CutShort, Refused(Forge(header.Replace("a1, 6", "a1, 7"))));

        // Bytes that are not UTF-8, in the source and in the header.
        var bytes = Encoding.UTF8.GetBytes(header);
        bytes[^3] = 0xFF;
        Assert.Equal(LinkProblem.NotText, Refused(Forge(bytes)));
        bytes = Encoding.UTF8.GetBytes(header);
        bytes[2] = 0xC3;
        Assert.Equal(LinkProblem.NotText, Refused(Forge(bytes)));
    }

    [Fact]
    public void ALinkIsNeverLetGrowPastItsLimits()
    {
        // Too many characters is refused before anything is decoded.
        Assert.Equal(LinkProblem.TooLong, Refused("1." + new string('A', ShareLink.MostCharacters)));

        // A few hundred characters that inflate to sixteen megabytes: inflating stops at the
        // limit, long before the end, and the link is refused.
        var bomb = Forge(new byte[16 * 1024 * 1024]);
        Assert.InRange(bomb.Length, 100, 30_000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Refused(bomb);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);

        // A source of exactly the most a link may hold is read; one byte more is not, even with
        // a check that is right for it.
        var most = new string('x', ShareLink.MostSourceBytes);
        Assert.Equal(most.Length, Decoded(Forge(Header(most))).Source.Length);

        var hash = default(Fetchline.Core.Trace.TraceHash);
        foreach (var value in Encoding.UTF8.GetBytes(most + "x"))
        {
            hash.Add(value);
        }

        var over = Header(most, change: $"check={hash.Value:x16}") + "x";
        Assert.Equal(LinkProblem.TooLong, Refused(Forge(over)));

        // The encoder refuses what it could not read back, and says why.
        Assert.Contains("128000 bytes", Assert.Throws<ArgumentException>(() => ShareLink.Encode(Simple with { Source = most + "x" })).Message);
        Assert.Throws<ArgumentException>(() => ShareLink.Encode(Simple with { Cycle = ShareLink.MostCycle + 1 }));
        Assert.Throws<ArgumentException>(() => ShareLink.Encode(Simple with { Config = new PipelineConfig { BtbEntries = 3 } }));

        // Source that does not compress can make a link too long to be read: refused when made.
        var random = new SeededRandom(0xF37C_8601);
        var noise = string.Concat(Enumerable.Range(0, 60_000).Select(_ => (char)random.Next(0x21, 0x7E)));
        Assert.Contains("characters long", Assert.Throws<ArgumentException>(() => ShareLink.Encode(Simple with { Source = noise })).Message);
    }

    [Fact]
    public void WhateverIsDoneToALinkReadingItNeitherThrowsNorAcceptsNonsense()
    {
        var random = new SeededRandom(0xF37C_8602);
        var links = ExampleCatalog.All.Select(example => ShareLink.Encode(new Shared(example.Source, PipelineConfig.Default, 7))).ToList();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.#+/= \n%";
        var accepted = 0;

        for (var round = 0; round < 6000; round++)
        {
            var link = new StringBuilder(random.Pick(links));
            for (var changes = random.Next(1, 4); changes > 0 && link.Length > 0; changes--)
            {
                var at = random.Next(0, link.Length - 1);
                switch (random.Next(0, 4))
                {
                    case 0:
                        link[at] = random.Pick(alphabet.ToCharArray());
                        break;
                    case 1:
                        link.Remove(at, Math.Min(link.Length - at, random.Next(1, 20)));
                        break;
                    case 2:
                        link.Insert(at, random.Pick(alphabet.ToCharArray()));
                        break;
                    case 3:
                        link.Length = at;                       // cut short
                        break;
                    default:
                        (link[at], link[0]) = (link[0], link[at]);
                        break;
                }
            }

            var text = link.ToString();
            bool read;
            Shared? shared;
            try
            {
                read = ShareLink.TryDecode(text, out shared, out _);
            }
            catch (Exception exception)
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: reading '{text}' threw {exception.GetType().Name}: {exception.Message}");
                return;
            }

            if (!read)
            {
                continue;
            }

            // What was accepted is within every limit, and writing it out and reading it back
            // gives the same thing: it is something this code could have written.
            accepted++;
            shared!.Config.Validate();
            Assert.True(shared.Cycle <= ShareLink.MostCycle);
            Assert.True(Encoding.UTF8.GetByteCount(shared.Source) <= ShareLink.MostSourceBytes);
            Assert.Equal(shared, Decoded(ShareLink.Encode(shared)));
        }

        // The check on the source catches nearly every change; what gets through is a change
        // that left the bytes that matter alone, such as one to the last character's spare bits.
        Assert.InRange(accepted, 0, 600);
    }

    [Fact]
    public void RandomTextIsNeverALink()
    {
        var random = new SeededRandom(0xF37C_8603);
        for (var round = 0; round < 3000; round++)
        {
            var length = random.Next(0, 300);
            var text = "1." + string.Concat(Enumerable.Range(0, length).Select(_ =>
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[random.Next(0, 63)]));

            Refused(text);
        }
    }
}
