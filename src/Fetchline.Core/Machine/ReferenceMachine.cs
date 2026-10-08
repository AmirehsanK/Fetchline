using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Core.Trace;

namespace Fetchline.Core.Machine;

/// <summary>
/// The reference machine: one whole instruction per step, with nothing between fetch and commit
/// that could go wrong. It is what the official tests are run on first, and what the pipeline is
/// then compared with, instruction by instruction.
/// </summary>
public sealed class ReferenceMachine
{
    // Decoded instructions, kept by address. An entry also remembers the word it was decoded
    // from, so a program that rewrites its own code is still executed correctly.
    private const int CacheSize = 4096;
    private readonly CacheEntry[] _cache = new CacheEntry[CacheSize];
    private Commit _last;

    public ReferenceMachine(Program program, TextWriter? output = null)
    {
        Hart = new Hart(program, output);
    }

    public Hart Hart { get; }

    /// <summary>Why the machine stopped, or <see cref="StopReason.None"/> while it is running or paused.</summary>
    public StopReason Stopped { get; private set; }

    /// <summary>Whether the machine has stopped for good.</summary>
    public bool IsFinished => Stopped is not (StopReason.None or StopReason.Breakpoint);

    /// <summary>
    /// Runs one instruction and returns what it did. Stepping a machine that has finished returns
    /// its last record again and changes nothing.
    /// </summary>
    public Commit Step()
    {
        if (IsFinished)
        {
            return _last;
        }

        var hart = Hart;
        var pc = hart.Pc;
        Commit commit;

        if (!hart.TryFetch(pc, out var word, out var stop))
        {
            commit = stop;
        }
        else
        {
            ref var entry = ref _cache[(pc >> 2) & (CacheSize - 1)];
            if (entry.Pc != pc || entry.Instruction.Raw != word || !entry.Valid)
            {
                entry = new CacheEntry(pc, Decoder.Decode(word), true);
            }

            var instruction = entry.Instruction;
            var control = instruction.Control;
            var x = hart.X;
            var (rs1, rs2) = (x[instruction.Rs1], x[instruction.Rs2]);

            var alu = Exec.Alu(
                control.Alu,
                Exec.OperandA(control.SrcA, rs1, pc),
                Exec.OperandB(control.SrcB, rs2, instruction.Imm));
            var taken = Exec.Taken(control.Branch, rs1, rs2);
            var target = Exec.Target(control.Branch, pc, instruction.Imm, alu);

            commit = hart.Complete(pc, instruction, rs1, rs2, alu, taken, target);
            if (commit.Register != 0)
            {
                x[commit.Register] = commit.Value;
            }
        }

        hart.Pc = commit.NextPc;
        hart.Cycle++;
        Stopped = commit.Stop;
        _last = commit;
        return commit;
    }

    /// <summary>
    /// Runs until the machine stops, or for at most <paramref name="maxInstructions"/> steps.
    /// Returns the last record; its <see cref="Commit.Stop"/> is <see cref="StopReason.None"/>
    /// when the budget ran out first.
    /// </summary>
    public Commit Run(ulong maxInstructions = ulong.MaxValue)
    {
        var commit = _last;
        for (ulong i = 0; i < maxInstructions; i++)
        {
            commit = Step();
            if (commit.Stop != StopReason.None)
            {
                break;
            }
        }

        return commit;
    }

    private readonly record struct CacheEntry(uint Pc, Instruction Instruction, bool Valid);
}
