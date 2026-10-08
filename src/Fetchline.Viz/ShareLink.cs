using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz;

/// <summary>What a link holds: a program, how the pipeline is built, and the cycle on screen.</summary>
public sealed record Shared(string Source, PipelineConfig Config, ulong Cycle);

/// <summary>Why a link could not be read.</summary>
public enum LinkProblem : byte
{
    None,

    /// <summary>There is nothing in it.</summary>
    Empty,

    /// <summary>It is longer, or holds more, than a link may.</summary>
    TooLong,

    /// <summary>It is a version of link this code does not read.</summary>
    UnknownKind,

    /// <summary>It does not unpack: it is damaged, or was never a link.</summary>
    Damaged,

    /// <summary>What it unpacks to is not text.</summary>
    NotText,

    /// <summary>It has a setting, or a value for one, that there is not.</summary>
    UnknownSettings,

    /// <summary>Its source is not the source it was made with: something was lost or changed on the way.</summary>
    CutShort,
}

/// <summary>
/// Turns what is on screen into a piece of text that fits in the fragment of a web address, and
/// back. The text is a version, a dot, and the rest in base64 made safe for addresses; the rest
/// is a few lines of <c>name=value</c>, a blank line and the source, deflated.
///
/// A link is untrusted input: anyone can write one. So reading one never throws, checks its
/// length before decoding it, stops inflating at a limit instead of trusting what the data says
/// its size is, and checks every value before giving it to anything that runs. Whatever it does
/// accept, it could have written itself.
/// </summary>
public static class ShareLink
{
    /// <summary>The version this code writes, and the only one it reads.</summary>
    public const string Version = "1";

    /// <summary>The longest link that is read, in characters.</summary>
    public const int MostCharacters = 48_000;

    /// <summary>The longest source a link may hold, in bytes of UTF-8: far more than any program typed here.</summary>
    public const int MostSourceBytes = 128_000;

    /// <summary>The furthest cycle a link may point at. Opening it means running that far.</summary>
    public const ulong MostCycle = 250_000;

    // The header is a handful of short lines; this is room to spare for it.
    private const int MostHeaderBytes = 1_000;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The text of a link. Throws if what it is given could not be read back.</summary>
    public static string Encode(Shared shared)
    {
        shared.Config.Validate();
        if (shared.Cycle > MostCycle)
        {
            throw new ArgumentException($"A link can point no further than cycle {MostCycle}.");
        }

        var source = Utf8.GetBytes(shared.Source);
        if (source.Length > MostSourceBytes)
        {
            throw new ArgumentException($"A link can hold a source of at most {MostSourceBytes} bytes.");
        }

        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"hazards={SwitchNames.Of(shared.Config.Hazards)}\nbranch={SwitchNames.Of(shared.Config.Branches)}\n" +
            $"predictor={SwitchNames.Of(shared.Config.Predictor)}\nbtb={shared.Config.BtbEntries}\n" +
            $"muldiv={shared.Config.MulDivCycles}\ncycle={shared.Cycle}\ncheck={Check(source)}\n\n");

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Utf8.GetBytes(header));
            deflate.Write(source);
        }

        var text = Version + "." + Convert.ToBase64String(packed.GetBuffer().AsSpan(0, (int)packed.Length))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return text.Length <= MostCharacters
            ? text
            : throw new ArgumentException($"The link would be {text.Length} characters long, and at most {MostCharacters} are read.");
    }

    /// <summary>
    /// Reads a link, with or without the <c>#</c> a fragment begins with. False, and what is
    /// wrong with it, for anything that is not a link this code could have written.
    /// </summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out Shared? shared, out LinkProblem problem)
    {
        problem = Read(text, out shared);
        return problem == LinkProblem.None && shared is not null;
    }

    private static LinkProblem Read(string? text, out Shared? shared)
    {
        shared = null;
        if (string.IsNullOrEmpty(text) || text == "#")
        {
            return LinkProblem.Empty;
        }

        if (text.Length > MostCharacters + 1)
        {
            return LinkProblem.TooLong;
        }

        var link = text.AsSpan(text[0] == '#' ? 1 : 0);
        var dot = link.IndexOf('.');
        if (dot < 0 || !link[..dot].SequenceEqual(Version))
        {
            return LinkProblem.UnknownKind;
        }

        if (Unpack(link[(dot + 1)..]) is not { } payload)
        {
            return LinkProblem.Damaged;
        }

        // The header ends at the first blank line; everything after it is the source.
        var end = payload.AsSpan().IndexOf("\n\n"u8);
        if (end < 0 || end > MostHeaderBytes)
        {
            return LinkProblem.Damaged;
        }

        var sourceBytes = payload.AsSpan(end + 2);
        if (sourceBytes.Length > MostSourceBytes)
        {
            return LinkProblem.TooLong;
        }

        string header, source;
        try
        {
            header = Utf8.GetString(payload, 0, end);
            source = Utf8.GetString(sourceBytes);
        }
        catch (ArgumentException)
        {
            return LinkProblem.NotText;
        }

        var config = PipelineConfig.Default;
        ulong cycle = 0;
        string? check = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in header.Split('\n'))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0 || !seen.Add(line[..equals]))
            {
                return LinkProblem.UnknownSettings;
            }

            var (name, value) = (line[..equals], line[(equals + 1)..]);
            switch (name)
            {
                case "hazards" when SwitchNames.TryParse<HazardHandling>(value, SwitchNames.Of, out var hazards):
                    config = config with { Hazards = hazards };
                    break;
                case "branch" when SwitchNames.TryParse<BranchDecision>(value, SwitchNames.Of, out var branches):
                    config = config with { Branches = branches };
                    break;
                case "predictor" when SwitchNames.TryParse<Predictor>(value, SwitchNames.Of, out var predictor):
                    config = config with { Predictor = predictor };
                    break;
                case "btb" when Number(value) is { } entries && entries <= 65536:
                    config = config with { BtbEntries = (int)entries };
                    break;
                case "muldiv" when Number(value) is { } cycles && cycles <= PipelineConfig.MaxMulDivCycles:
                    config = config with { MulDivCycles = (int)cycles };
                    break;
                case "cycle" when Number(value) is { } at && at <= MostCycle:
                    cycle = at;
                    break;
                case "check":
                    check = value;
                    break;
                default:
                    return LinkProblem.UnknownSettings;
            }
        }

        try
        {
            config.Validate();
        }
        catch (ArgumentException)
        {
            return LinkProblem.UnknownSettings;
        }

        // Deflate carries no checksum of its own, and a link cut short by whatever passed it on
        // can still inflate to the first part of a program. The source has to be the one hashed.
        if (check != Check(sourceBytes))
        {
            return LinkProblem.CutShort;
        }

        shared = new Shared(source, config, cycle);
        return LinkProblem.None;
    }

    /// <summary>
    /// A number written as plain digits and nothing else: no sign, no spaces, no exponent, and
    /// no zero in front, which is not how this code writes one.
    /// </summary>
    private static ulong? Number(string text) =>
        text.Length is > 0 and <= 10 && (text.Length == 1 || text[0] != '0')
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>A hash of the source's bytes, as sixteen hex digits: FNV-1a, the hash traces use.</summary>
    private static string Check(ReadOnlySpan<byte> source)
    {
        var hash = default(Core.Trace.TraceHash);
        foreach (var value in source)
        {
            hash.Add(value);
        }

        return hash.Value.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Undoes the base64 and the deflating, or gives null. Inflating stops at the most a link
    /// may hold, whatever the data claims: a few bytes can be made to inflate to gigabytes.
    /// </summary>
    private static byte[]? Unpack(ReadOnlySpan<char> packed)
    {
        // Base64 for addresses: '-' and '_' for '+' and '/', and no padding.
        if (packed.Length == 0 || packed.Length % 4 == 1)
        {
            return null;
        }

        var padded = new char[(packed.Length + 3) / 4 * 4];
        Array.Fill(padded, '=');
        for (var i = 0; i < packed.Length; i++)
        {
            var c = packed[i];
            if (c == '-')
            {
                padded[i] = '+';
            }
            else if (c == '_')
            {
                padded[i] = '/';
            }
            else if (char.IsAsciiLetterOrDigit(c))
            {
                padded[i] = c;
            }
            else
            {
                // Not in the alphabet: the ordinary one's '+' and '/', a space, anything else.
                return null;
            }
        }

        var bytes = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(padded, bytes, out var written))
        {
            return null;
        }

        var limit = MostHeaderBytes + 2 + MostSourceBytes;
        var buffer = new byte[Math.Min(limit + 1, 4096)];
        try
        {
            using var inflate = new DeflateStream(new MemoryStream(bytes, 0, written), CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            int read;
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                unpacked.Write(buffer, 0, read);
                if (unpacked.Length > limit)
                {
                    return null;
                }
            }

            return unpacked.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
