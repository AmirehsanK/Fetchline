using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;

namespace Fetchline.Benchmarks;

// The figures in docs/CPU.md come from here. Run every benchmark with
//
//   dotnet run -c Release --project bench/Fetchline.Benchmarks -- --filter *
//
// and copy the table into the document when the engine has changed.
internal static class EntryPoint
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(EntryPoint).Assembly).Run(args);

    public static string Example(string name) => Path.Combine(
        typeof(EntryPoint).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!,
        "examples",
        name);
}

/// <summary>The reference machine running a whole program: the sieve in examples/primes.s.</summary>
[MemoryDiagnoser]
public class InterpreterBenchmarks
{
    private Program _primes = null!;

    /// <summary>How many instructions one run of the program executes, to turn a time into a rate.</summary>
    public static ulong InstructionsPerRun { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        _primes = Assembler.Assemble(File.ReadAllText(EntryPoint.Example("primes.s"))).Program!;
        InstructionsPerRun = Primes();
    }

    [Benchmark]
    public ulong Primes()
    {
        var machine = new ReferenceMachine(_primes, TextWriter.Null);
        machine.Run();
        return machine.Hart.InstructionsRetired;
    }
}

/// <summary>Decoding alone: 1,024 words, a mix of every instruction with a few illegal ones.</summary>
public class DecoderBenchmarks
{
    private const int Count = 1024;
    private readonly uint[] _words = new uint[Count];

    [GlobalSetup]
    public void Setup()
    {
        // A fixed linear congruential sequence: the same words on every run and every machine.
        var state = 0x2545_F491u;
        for (var i = 0; i < Count; i++)
        {
            state = (state * 1_664_525) + 1_013_904_223;
            var row = InstructionSet.All[(int)(state >> 8) % InstructionSet.All.Count];
            _words[i] = (state & ~row.Mask) | row.Match;
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int Decode()
    {
        var sum = 0;
        foreach (var word in _words)
        {
            sum += (int)Decoder.Decode(word).Op;
        }

        return sum;
    }
}

/// <summary>Assembling a source of about fifty lines: examples/bubble-sort.s.</summary>
[MemoryDiagnoser]
public class AssemblerBenchmarks
{
    private string _source = null!;

    [GlobalSetup]
    public void Setup() => _source = File.ReadAllText(EntryPoint.Example("bubble-sort.s"));

    [Benchmark]
    public bool Assemble() => Assembler.Assemble(_source).Success;
}
