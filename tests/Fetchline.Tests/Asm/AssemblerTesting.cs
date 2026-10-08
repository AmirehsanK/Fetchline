using Fetchline.Core.Asm;

namespace Fetchline.Tests.Asm;

/// <summary>What the assembler's tests all do: assemble, and look at the program or the diagnostics.</summary>
internal static class AssemblerTesting
{
    /// <summary>Assembles source that must be free of errors, and returns the program.</summary>
    public static Program Assemble(string source, AssemblerOptions? options = null)
    {
        var result = Assembler.Assemble(source, options);
        Assert.True(result.Success, "expected no errors, but got:\n" + result.RenderDiagnostics());
        return result.Program!;
    }

    /// <summary>Assembles source that must have errors, and returns its diagnostics.</summary>
    public static IReadOnlyList<Diagnostic> Diagnose(string source)
    {
        var result = Assembler.Assemble(source);
        Assert.False(result.Success, "expected an error, but the source assembled");
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, d => d.Severity == Severity.Error);

        // Every diagnostic must be printable against its own source.
        Assert.NotEmpty(result.RenderDiagnostics("test.s"));
        return result.Diagnostics;
    }

    /// <summary>Assembles source that must have exactly one diagnostic, and returns it.</summary>
    public static Diagnostic DiagnoseOne(string source) => Assert.Single(Diagnose(source));

    public static Segment Segment(this Program program, string name) =>
        program.Segments.Single(segment => segment.Name == name);

    public static byte[] Bytes(this Program program, string segment) => program.Segment(segment).Data.ToArray();

    public static uint AddressOf(this Program program, string symbol)
    {
        Assert.True(program.TryGetSymbol(symbol, out var found), $"no symbol '{symbol}'");
        return found.Value;
    }

    /// <summary>The words of the text segment.</summary>
    public static uint[] Words(this Program program)
    {
        var text = program.Text!;
        var words = new uint[text.Size / 4];
        for (var i = 0; i < words.Length; i++)
        {
            Assert.True(program.TryReadWord(text.Address + (uint)(4 * i), out words[i]));
        }

        return words;
    }
}
