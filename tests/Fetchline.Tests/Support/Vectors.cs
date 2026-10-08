namespace Fetchline.Tests.Support;

/// <summary>
/// The official test programs under <c>tests/vectors/riscv-tests</c>, built by the Vectors
/// workflow from a pinned commit. See <c>tests/vectors/PROVENANCE.md</c>.
/// </summary>
internal static class Vectors
{
    private static readonly string Root = Repo.PathOf("tests", "vectors", "riscv-tests");

    /// <summary>Every test's name, such as <c>rv32ui-p-add</c>, in order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        .. Directory.GetFiles(Path.Combine(Root, "elf"))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The names as rows for a theory.</summary>
    public static TheoryData<string> Rows() => [.. Names];

    public static byte[] Elf(string name) => File.ReadAllBytes(Path.Combine(Root, "elf", name));

    /// <summary>What <c>objdump -d -M no-aliases</c> printed for the test.</summary>
    public static string[] Dump(string name) => File.ReadAllLines(Path.Combine(Root, "dump", name + ".dump"));
}
