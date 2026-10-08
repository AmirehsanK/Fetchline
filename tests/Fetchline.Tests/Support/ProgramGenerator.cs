using System.Globalization;
using System.Text;

namespace Fetchline.Tests.Support;

/// <summary>
/// Random programs that always end, for the tests that run one program on the pipeline built
/// many ways. They are made of everything the hazard logic has to get right: arithmetic that
/// depends on what was just computed, loads and stores, multiplies and divides, forward branches
/// on data, counted loops inside loops, calls and returns, CSR accesses and system calls.
///
/// Three rules make a program end whatever its data turns out to be. A branch on data only goes
/// forwards. A loop is closed by a counter in a register nothing else writes. And a label is
/// only ever jumped to from the sequence it is in, or from inside a loop to just past its end,
/// so no jump lands inside a loop whose counter has not been set.
/// </summary>
internal sealed class ProgramGenerator
{
    // What the random instructions read and write: few registers, so that an instruction often
    // needs what the one or two before it produced.
    private static readonly string[] Pool = ["a0", "a1", "a2", "a3", "t0", "t1", "zero"];

    // Kept out of the pool: s0 holds the address of the data, s1 to s3 count loops, t2 is the
    // address of an indexed access, t6 the target of an indirect call, a7 the system call
    // number, and ra the way back from a function.
    private static readonly string[] Counters = ["s1", "s2"];
    private const string FunctionCounter = "s3";

    private static readonly string[] RegisterOps = ["add", "sub", "and", "or", "xor", "sll", "srl", "sra", "slt", "sltu"];
    private static readonly string[] ImmediateOps = ["addi", "andi", "ori", "xori", "slti", "sltiu"];
    private static readonly string[] ShiftOps = ["slli", "srli", "srai"];
    private static readonly string[] MulDivOps = ["mul", "mulh", "mulhsu", "mulhu", "div", "divu", "rem", "remu"];
    private static readonly string[] Conditions = ["beq", "bne", "blt", "bge", "bltu", "bgeu", "bgt", "ble", "bgtu", "bleu"];
    private static readonly string[] ZeroConditions = ["beqz", "bnez", "bltz", "bgez", "blez", "bgtz"];
    private static readonly (string Mnemonic, int Bytes)[] Loads = [("lw", 4), ("lh", 2), ("lhu", 2), ("lb", 1), ("lbu", 1)];
    private static readonly (string Mnemonic, int Bytes)[] Stores = [("sw", 4), ("sh", 2), ("sb", 1)];

    private const int Cells = 16;
    private const int Functions = 3;

    /// <summary>Where the assembler puts the data, and so where the cells are: they come first.</summary>
    public const uint DataBase = 0x1000_0000;

    private readonly SeededRandom _random;
    private readonly StringBuilder _text = new();
    private int _labels;

    // The registers the last two instructions wrote, the older first.
    private string? _written;
    private string? _writtenBefore;

    private ProgramGenerator(SeededRandom random) => _random = random;

    /// <summary>The source of one program. The same generator state gives the same program.</summary>
    public static string Generate(SeededRandom random)
    {
        var generator = new ProgramGenerator(random);
        generator.Program();
        return generator._text.ToString();
    }

    private void Program()
    {
        Line(".data");
        Line("cells: .word " + string.Join(", ", Enumerable.Range(0, Cells).Select(_ => Constant())));
        Line(".text");

        // The address of the data, in both the registers that hold addresses. It is written with
        // no dependency and given time to land, so that even a pipeline with its hazard handling
        // off has it right, and goes wrong later, in more interesting ways than a first store
        // to nowhere.
        Line($"lui s0, 0x{DataBase >> 12:x}");
        Line($"lui t2, 0x{DataBase >> 12:x}");
        Line("nop");
        Line("nop");
        Line("nop");
        foreach (var register in Pool.Where(register => register != "zero"))
        {
            if (_random.Chance(60))
            {
                Line($"li {register}, {Constant()}");
            }
        }

        Sequence(_random.Next(10, 22), depth: 0, inFunction: false, breakTo: null);

        // Three ways to end: run off the end of the code, or leave through either exit call.
        switch (_random.Next(0, 2))
        {
            case 0:
                Line("j finish");
                break;
            case 1:
                Line("li a7, 10");
                Line("ecall");
                break;
            default:
                Line($"li a0, {_random.Next(0, 3)}");
                Line("li a7, 93");
                Line("ecall");
                break;
        }

        // The functions come after the code that calls them, and are reached no other way.
        for (var i = 0; i < Functions; i++)
        {
            Line($"f{i}:");
            Sequence(_random.Next(1, 6), depth: 0, inFunction: true, breakTo: null);
            Line(_random.NextBool() ? "ret" : "jr ra");
        }

        Line("finish:");
    }

    /// <summary>
    /// A run of items. A forward branch in it skips some of the items after it and lands on a
    /// label that belongs to this run, placed before the item it names or at the end.
    /// </summary>
    private void Sequence(int count, int depth, bool inFunction, string? breakTo)
    {
        var landing = new List<string>[count + 1];
        string LandAfter(int item)
        {
            var label = $"L{_labels++}";
            (landing[Math.Min(count, item + 1 + _random.Next(0, 3))] ??= []).Add(label);
            return label;
        }

        for (var item = 0; item < count; item++)
        {
            foreach (var label in landing[item] ?? [])
            {
                Line(label + ":");
            }

            switch (_random.Next(0, 99))
            {
                case < 24:
                    Arithmetic();
                    break;
                case < 31:
                    Line(Into($"{_random.Pick(MulDivOps)} {{0}}, {S()}, {S()}"));
                    break;
                case < 40:
                    Load();
                    break;
                case < 47:
                    Store();
                    break;
                case < 53:
                    Indexed();
                    break;
                case < 64:
                    Line(_random.Chance(70)
                        ? $"{_random.Pick(Conditions)} {S()}, {S()}, {LandAfter(item)}"
                        : $"{_random.Pick(ZeroConditions)} {S()}, {LandAfter(item)}");
                    break;
                case < 67:
                    Line($"j {LandAfter(item)}");
                    break;
                case < 70 when breakTo is not null:
                    // Out of the loop early, on data.
                    Line($"{_random.Pick(Conditions)} {S()}, {S()}, {breakTo}");
                    break;
                case < 79 when depth < (inFunction ? 1 : 2):
                    Loop(depth, inFunction);
                    break;
                case < 86 when !inFunction:
                    Call();
                    break;
                case < 91:
                    Csr();
                    break;
                case < 95:
                    Print();
                    break;
                case < 96:
                    Line("fence.i");
                    break;
                default:
                    Arithmetic();
                    break;
            }
        }

        foreach (var label in landing[count] ?? [])
        {
            Line(label + ":");
        }
    }

    private void Arithmetic()
    {
        switch (_random.Next(0, 9))
        {
            case < 4:
                Line(Into($"{_random.Pick(RegisterOps)} {{0}}, {S()}, {S()}"));
                break;
            case < 7:
                Line(Into($"{_random.Pick(ImmediateOps)} {{0}}, {S()}, {_random.Next(-2048, 2047)}"));
                break;
            case 7:
                Line(Into($"{_random.Pick(ShiftOps)} {{0}}, {S()}, {_random.Next(0, 31)}"));
                break;
            case 8:
                Line(Into($"lui {{0}}, {_random.Next(0, 0xFFFFF)}"));
                break;
            default:
                Line(Into(_random.NextBool() ? $"auipc {{0}}, {_random.Next(0, 0xFFFFF)}" : $"mv {{0}}, {S()}"));
                break;
        }
    }

    // Every access is inside the cells, at any alignment: a misaligned access is allowed here.
    private void Load()
    {
        var (mnemonic, bytes) = _random.Pick(Loads);
        Line(Into($"{mnemonic} {{0}}, {_random.Next(0, (4 * Cells) - bytes)}(s0)"));
    }

    private void Store()
    {
        var (mnemonic, bytes) = _random.Pick(Stores);
        Line($"{mnemonic} {S()}, {_random.Next(0, (4 * Cells) - bytes)}(s0)");
    }

    /// <summary>
    /// An access whose address is computed just before it, from data: the address itself is
    /// then something to forward, and a load can feed the address of the next one.
    /// </summary>
    private void Indexed()
    {
        Line($"andi t2, {S()}, {4 * (Cells - 1)}");
        Line("add t2, t2, s0");
        Line(_random.NextBool() ? Into("lw {0}, 0(t2)") : $"sw {S()}, 0(t2)");
    }

    private void Loop(int depth, bool inFunction)
    {
        var counter = inFunction ? FunctionCounter : Counters[depth];
        var (top, after) = ($"T{_labels}", $"X{_labels}");
        _labels++;

        Line($"li {counter}, {_random.Next(1, 4)}");
        Line(top + ":");
        Sequence(_random.Next(2, 6), depth + 1, inFunction, breakTo: after);
        Line($"addi {counter}, {counter}, -1");
        Line(_random.NextBool() ? $"bnez {counter}, {top}" : $"bgtz {counter}, {top}");
        Line(after + ":");
    }

    /// <summary>A call, direct or through a register. Functions call nothing, so ra is safe in them.</summary>
    private void Call()
    {
        var function = $"f{_random.Next(0, Functions - 1)}";
        switch (_random.Next(0, 3))
        {
            case 0:
                Line($"jal {function}");
                break;
            case 1:
                Line($"call {function}");
                break;
            case 2:
                Line($"la t6, {function}");
                Line("jalr t6");
                break;
            default:
                Line($"la t6, {function} + 4");
                Line("jalr ra, t6, -4");
                break;
        }
    }

    private void Csr()
    {
        switch (_random.Next(0, 9))
        {
            case 0:
                Line(Into($"csrrw {{0}}, mscratch, {S()}"));
                break;
            case 1:
                Line(Into($"csrrs {{0}}, mscratch, {S()}"));
                break;
            case 2:
                Line(Into($"csrrc {{0}}, mscratch, {S()}"));
                break;
            case 3:
                Line(Into($"csrrwi {{0}}, mscratch, {_random.Next(0, 31)}"));
                break;
            case 4:
                Line($"csrw mscratch, {S()}");
                break;
            case < 7:
                Line(Into("csrr {0}, mscratch"));
                break;
            case 7:
                Line(Into("rdinstret {0}"));
                break;
            default:
                // The one thing the two machines may see differently; the pipeline's reading is
                // the one the program goes on with.
                Line(Into("rdcycle {0}"));
                break;
        }
    }

    /// <summary>A system call that prints: a character, or whatever is in a register.</summary>
    private void Print()
    {
        if (_random.NextBool())
        {
            Line($"li a0, {(int)'A' + _random.Next(0, 25)}");
            Line("li a7, 11");
        }
        else
        {
            Line($"mv a0, {S()}");
            Line("li a7, 1");
        }

        Line("ecall");
    }

    /// <summary>
    /// A register to read. Two times in five it is one that the last instruction or the one
    /// before it wrote, which is what makes a hazard: left to chance alone, a use lands that
    /// close to its producer too seldom.
    /// </summary>
    private string S()
    {
        if (_random.Chance(40) && (_written ?? _writtenBefore) is not null)
        {
            return _random.NextBool() ? _written ?? _writtenBefore! : _writtenBefore ?? _written!;
        }

        return _random.Pick(Pool);
    }

    /// <summary>
    /// Puts a register to write where <c>{0}</c> stands in an instruction. It is chosen after the
    /// registers the instruction reads, so that "the last one written" never means its own.
    /// </summary>
    private string Into(string instruction)
    {
        var register = _random.Pick(Pool);
        (_writtenBefore, _written) = (_written, register == "zero" ? null : register);
        return string.Format(CultureInfo.InvariantCulture, instruction, register);
    }

    /// <summary>A constant that is small often enough for comparisons to come out both ways.</summary>
    private string Constant() => _random.Next(0, 3) switch
    {
        0 => _random.Next(-3, 3).ToString(CultureInfo.InvariantCulture),
        1 => _random.Next(-1000, 1000).ToString(CultureInfo.InvariantCulture),
        2 => "0x" + _random.NextUInt32().ToString("x8", CultureInfo.InvariantCulture),
        _ => ((int)_random.NextUInt32()).ToString(CultureInfo.InvariantCulture),
    };

    private void Line(string text) => _text.Append(text).Append('\n');
}
