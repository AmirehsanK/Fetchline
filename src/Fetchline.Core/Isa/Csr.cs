using System.Collections.Frozen;
using System.Globalization;

namespace Fetchline.Core.Isa;

/// <summary>
/// Control and status register numbers and their names. The table is wider than what the core
/// implements: the disassembler has to name the registers that real programs probe for, such as
/// <c>satp</c> or <c>pmpaddr0</c>, even though reading them here raises an illegal-instruction
/// trap. A test checks every entry against the official <c>csrs.csv</c>.
/// </summary>
public static class Csr
{
    public const int Sstatus = 0x100;
    public const int Satp = 0x180;

    public const int Mstatus = 0x300;
    public const int Misa = 0x301;
    public const int Medeleg = 0x302;
    public const int Mideleg = 0x303;
    public const int Mie = 0x304;
    public const int Mtvec = 0x305;
    public const int Mcounteren = 0x306;
    public const int Mstatush = 0x310;

    public const int Mscratch = 0x340;
    public const int Mepc = 0x341;
    public const int Mcause = 0x342;
    public const int Mtval = 0x343;
    public const int Mip = 0x344;

    public const int Tselect = 0x7A0;
    public const int Tdata1 = 0x7A1;

    public const int Mcycle = 0xB00;
    public const int Minstret = 0xB02;
    public const int Mcycleh = 0xB80;
    public const int Minstreth = 0xB82;

    public const int Cycle = 0xC00;
    public const int Time = 0xC01;
    public const int Instret = 0xC02;
    public const int Cycleh = 0xC80;
    public const int Timeh = 0xC81;
    public const int Instreth = 0xC82;

    public const int Mvendorid = 0xF11;
    public const int Marchid = 0xF12;
    public const int Mimpid = 0xF13;
    public const int Mhartid = 0xF14;
    public const int Mconfigptr = 0xF15;

    private static readonly FrozenDictionary<int, string> NameOf = BuildNames();
    private static readonly FrozenDictionary<string, int> NumberOf =
        NameOf.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Every name the assembler and the disassembler know, with its number.</summary>
    public static IReadOnlyDictionary<string, int> Names => NumberOf;

    /// <summary>
    /// A CSR's top two address bits say whether it can be written at all: 11 means read-only,
    /// and a write to one is an illegal instruction.
    /// </summary>
    public static bool IsReadOnly(int number) => (number & 0xC00) == 0xC00;

    /// <summary>The name of a CSR, when it has one here.</summary>
    public static bool TryGetName(int number, out string name) => NameOf.TryGetValue(number, out name!);

    /// <summary>The number of a CSR written by name.</summary>
    public static bool TryGetNumber(string name, out int number) => NumberOf.TryGetValue(name, out number);

    /// <summary>How a CSR is written in assembly: its name, or its number in hex when it has none.</summary>
    public static string Format(int number) =>
        NameOf.TryGetValue(number, out var name) ? name : "0x" + number.ToString("x3", CultureInfo.InvariantCulture);

    private static FrozenDictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            // Unprivileged counters
            [Cycle] = "cycle", [Time] = "time", [Instret] = "instret",
            [Cycleh] = "cycleh", [Timeh] = "timeh", [Instreth] = "instreth",

            // Supervisor: not implemented, but programs probe for them
            [Sstatus] = "sstatus", [0x104] = "sie", [0x105] = "stvec", [0x106] = "scounteren",
            [0x140] = "sscratch", [0x141] = "sepc", [0x142] = "scause", [0x143] = "stval", [0x144] = "sip",
            [Satp] = "satp",

            // Machine information
            [Mvendorid] = "mvendorid", [Marchid] = "marchid", [Mimpid] = "mimpid",
            [Mhartid] = "mhartid", [Mconfigptr] = "mconfigptr",

            // Machine trap setup
            [Mstatus] = "mstatus", [Misa] = "misa", [Medeleg] = "medeleg", [Mideleg] = "mideleg",
            [Mie] = "mie", [Mtvec] = "mtvec", [Mcounteren] = "mcounteren",
            [Mstatush] = "mstatush", [0x312] = "medelegh",

            // Machine trap handling
            [Mscratch] = "mscratch", [Mepc] = "mepc", [Mcause] = "mcause", [Mtval] = "mtval", [Mip] = "mip",

            // Machine configuration
            [0x30A] = "menvcfg", [0x31A] = "menvcfgh", [0x320] = "mcountinhibit",
            [0x747] = "mseccfg", [0x757] = "mseccfgh",

            // Resumable non-maskable interrupts
            [0x740] = "mnscratch", [0x741] = "mnepc", [0x742] = "mncause", [0x744] = "mnstatus",

            // Debug triggers and debug mode
            [Tselect] = "tselect", [Tdata1] = "tdata1", [0x7A2] = "tdata2", [0x7A3] = "tdata3",
            [0x7B0] = "dcsr", [0x7B1] = "dpc", [0x7B2] = "dscratch0", [0x7B3] = "dscratch1",

            // Machine counters
            [Mcycle] = "mcycle", [Minstret] = "minstret", [Mcycleh] = "mcycleh", [Minstreth] = "minstreth",
        };

        // Physical memory protection: four configuration registers on RV32, sixteen addresses.
        for (var i = 0; i < 4; i++)
        {
            names[0x3A0 + i] = "pmpcfg" + i.ToString(CultureInfo.InvariantCulture);
        }

        for (var i = 0; i < 16; i++)
        {
            names[0x3B0 + i] = "pmpaddr" + i.ToString(CultureInfo.InvariantCulture);
        }

        return names.ToFrozenDictionary();
    }
}
