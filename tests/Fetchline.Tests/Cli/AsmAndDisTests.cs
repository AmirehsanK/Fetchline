using System.Text.RegularExpressions;
using Fetchline.Core.Asm;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Cli;

public partial class AsmAndDisTests
{
    public static TheoryData<string> Examples() =>
        [.. Directory.GetFiles(Repo.PathOf("examples"), "*.s").Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryExampleAssemblesWithoutADiagnostic(string name)
    {
        var result = Assembler.Assemble(File.ReadAllText(Example(name)));

        Assert.True(result.Success, result.RenderDiagnostics(name));
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.Program!.SourceMap.Entries);
    }

    [Fact]
    public void AsmReportsWhatASourceBecame()
    {
        var path = Example("hello.s");

        Assert.Equal($"{path}: 6 instructions, 24 bytes of code, 16 bytes of data, entry 0x00000000\n", RunOk("asm", path));
    }

    [Fact]
    public void AsmPrintsAListingWhenAsked()
    {
        var listing = RunOk("asm", "--listing", Example("sum.s"));

        Assert.Equal(
            """
            00000000 <main>:
            00000000:  00000513  li      a0, 0
            00000004:  00100293  li      t0, 1
            00000008:  00a00313  li      t1, 10

            0000000c <loop>:
            0000000c:  00550533  add     a0, a0, t0
            00000010:  00128293  addi    t0, t0, 1
            00000014:  fe535ce3  bge     t1, t0, loop     # ble t0, t1, loop
            00000018:  00100893  li      a7, 1
            0000001c:  00000073  ecall
            00000020:  00a00513  li      a0, 10           # li a0, '\n'
            00000024:  00b00893  li      a7, 11
            00000028:  00000073  ecall
            0000002c:  00a00893  li      a7, 10
            00000030:  00000073  ecall

            """.ReplaceLineEndings("\n"),
            listing);
    }

    [Fact]
    public void AnElfFileWrittenByAsmDisassemblesToTheSameCode()
    {
        using var elf = new TemporaryFile(extension: ".elf");
        var path = Example("factorial.s");

        Assert.Equal(RunOk("asm", path), RunOk("asm", path, "-o", elf.Path));
        var fromElf = RunOk("dis", elf.Path);
        var fromSource = RunOk("dis", path);

        // The source is not in the file, so the listing of the file has no source beside it.
        Assert.Equal(SourceComment().Replace(fromSource, string.Empty), fromElf);
        Assert.Contains("<factorial>:", fromElf);
        Assert.Contains("mul     a0, a0, t1", fromElf);
    }

    [Fact]
    public void DisCanShowRealInstructionsAndNumericRegisters()
    {
        using var source = Source("main:\n    li a0, 5\n    ret\n");

        Assert.Equal(
            "00000000 <main>:\n00000000:  00500513  li      a0, 5\n00000004:  00008067  ret\n",
            RunOk("dis", source.Path));
        Assert.Equal(
            "00000000 <main>:\n" +
            "00000000:  00500513  addi    x10, x0, 5       # li a0, 5\n" +
            "00000004:  00008067  jalr    x0, 0(x1)        # ret\n",
            RunOk("dis", "--no-aliases", "--numeric", source.Path));
    }

    [Fact]
    public void ErrorsInTheSourceGoToTheErrorStreamWithTheFileAndPosition()
    {
        using var source = Source("main:\n    adid a0, a0, 1\n    lw   a1, 8(sq)\n");

        var (exitCode, output, error) = Run("asm", source.Path);

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Equal(
            $"{source.Path}:2:5: error: unknown instruction 'adid'\n" +
            "      adid a0, a0, 1\n" +
            "      ^~~~\n" +
            "  did you mean 'addi'?\n" +
            $"{source.Path}:3:16: error: expected a base register\n" +
            "      lw   a1, 8(sq)\n" +
            "                 ^~\n" +
            "  did you mean 'sp'?\n" +
            $"fetchline: 2 errors in '{source.Path}'\n",
            error);
    }

    [Fact]
    public void AMissingFileIsAUsageProblemNotAnErrorInTheProgram()
    {
        var (exitCode, output, error) = Run("asm", Example("no-such-file.s"));

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("fetchline: cannot read '", error);
    }

    [Fact]
    public void ADamagedElfFileIsRefusedWithAReason()
    {
        using var file = new TemporaryFile();
        File.WriteAllBytes(file.Path, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, .. new byte[60]]);

        var (exitCode, _, error) = Run("dis", file.Path);

        Assert.Equal(1, exitCode);
        Assert.Equal($"fetchline: '{file.Path}' is a 64-bit ELF file; this core is RV32\n", error);
    }

    [Fact]
    public void TheCommandLineExplainsItself()
    {
        var help = RunOk("--help");

        Assert.Contains("asm", help);
        Assert.Contains("dis", help);
        Assert.Contains("Assemble a source file", RunOk("asm", "--help"));
    }

    [GeneratedRegex(@" +# .*$", RegexOptions.Multiline)]
    private static partial Regex SourceComment();
}
