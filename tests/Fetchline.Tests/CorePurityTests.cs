using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Fetchline.Core.Isa;

namespace Fetchline.Tests;

/// <summary>
/// The engine must behave the same in the tests, the command line and the browser, so it may not
/// touch files, the console, the clock, the environment or the platform's random numbers. This
/// reads the compiled assembly and fails on the first reference to any of them.
/// </summary>
public class CorePurityTests
{
    private static readonly string[] BannedTypes =
    [
        "System.Console",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Environment",
        "System.Guid",
        "System.Random",
        "System.TimeProvider",
        "System.Diagnostics.Process",
        "System.Diagnostics.Stopwatch",
        "System.Threading.Thread",
        "System.Threading.Timer",
    ];

    private static readonly string[] BannedNamespaces =
    [
        "System.IO.Compression",
        "System.IO.Pipes",
        "System.Net",
        "System.Security.Cryptography",
        "System.Threading.Tasks",
    ];

    // System.IO is banned except for the in-memory pieces, which do no I/O.
    private static readonly string[] AllowedIoTypes =
    [
        "System.IO.EndOfStreamException",
        "System.IO.InvalidDataException",
        "System.IO.StringReader",
        "System.IO.StringWriter",
        "System.IO.TextReader",
        "System.IO.TextWriter",
    ];

    [Fact]
    public void CoreReferencesNoPackagesOnlyTheFramework()
    {
        using var pe = OpenCore();
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.StartsWith("System.", name));
    }

    [Fact]
    public void CoreUsesNoFilesClockEnvironmentOrPlatformRandomness()
    {
        using var pe = OpenCore();
        var metadata = pe.GetMetadataReader();
        var offenders = new List<string>();

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(reference.Namespace);
            var fullName = ns + "." + metadata.GetString(reference.Name);

            var banned = BannedTypes.Contains(fullName)
                || BannedNamespaces.Any(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal))
                || (ns == "System.IO" && !AllowedIoTypes.Contains(fullName));
            if (banned)
            {
                offenders.Add(fullName);
            }
        }

        Assert.True(offenders.Count == 0, "Fetchline.Core references: " + string.Join(", ", offenders));
    }

    private static PEReader OpenCore() => new(File.OpenRead(typeof(Registers).Assembly.Location));
}
