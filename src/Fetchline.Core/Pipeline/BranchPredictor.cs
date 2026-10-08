using Fetchline.Core.Isa;

namespace Fetchline.Core.Pipeline;

/// <summary>
/// Guesses, at fetch, where the instruction after this one is. A right guess makes a taken
/// branch free; a wrong one is found when the branch is decided, and costs what an unpredicted
/// taken branch costs.
///
/// The two dynamic predictors keep a branch target buffer: a small table, indexed by the low
/// bits of the address, that remembers for a branch seen before where it went and whether to
/// expect it to go there again. A real one is looked up with the address alone, before the
/// instruction is even decoded, and so is this one.
/// </summary>
internal sealed class BranchPredictor
{
    private readonly Predictor _kind;
    private readonly Entry[] _table;

    public BranchPredictor(PipelineConfig config)
    {
        _kind = config.Predictor;
        _table = _kind is Predictor.OneBit or Predictor.TwoBit ? new Entry[config.BtbEntries] : [];
    }

    /// <summary>The address fetch should go to after the instruction at <paramref name="pc"/>.</summary>
    public uint Predict(uint pc, uint word)
    {
        switch (_kind)
        {
            case Predictor.BackwardTaken:
            {
                // A static rule read off the instruction itself: a conditional branch that goes
                // backwards is nearly always the foot of a loop, so it is taken; one that goes
                // forwards is not. A jal always goes where it says.
                var instruction = Decoder.Decode(word);
                var kind = instruction.Control.Branch;
                var follow = kind == BranchKind.Jal || (instruction.Control.IsConditionalBranch && instruction.Imm < 0);
                return follow ? pc + (uint)instruction.Imm : pc + 4;
            }

            case Predictor.OneBit or Predictor.TwoBit:
            {
                var entry = _table[Index(pc)];
                return entry.Valid && entry.Tag == pc && entry.State >= TakenFrom ? entry.Target : pc + 4;
            }

            default:
                return pc + 4;
        }
    }

    /// <summary>Tells the predictor how a branch or jump came out.</summary>
    public void Update(uint pc, bool taken, uint target)
    {
        if (_table.Length == 0)
        {
            return;
        }

        ref var entry = ref _table[Index(pc)];
        var known = entry.Valid && entry.Tag == pc;

        if (!known)
        {
            // Only a branch that was taken is worth a place in the table: for anything else the
            // default guess, straight on, is already right. A new entry starts out believing the
            // branch, but in the two-bit case only weakly.
            if (taken)
            {
                entry = new Entry(true, pc, target, _kind == Predictor.OneBit ? (byte)1 : (byte)2);
            }

            return;
        }

        if (_kind == Predictor.OneBit)
        {
            // One bit: do next time what it did this time.
            entry = entry with { State = (byte)(taken ? 1 : 0) };
        }
        else
        {
            // Two bits: a counter that saturates at 0 and 3. It takes two surprises in a row to
            // change its mind, so the one odd outcome at the end of a loop does not.
            var state = taken ? Math.Min(3, entry.State + 1) : Math.Max(0, entry.State - 1);
            entry = entry with { State = (byte)state };
        }

        if (taken)
        {
            // An indirect jump may go somewhere new each time; remember the latest.
            entry = entry with { Target = target };
        }
    }

    /// <summary>The lowest counter value that means "expect it to be taken".</summary>
    private byte TakenFrom => _kind == Predictor.OneBit ? (byte)1 : (byte)2;

    private int Index(uint pc) => (int)((pc >> 2) & (uint)(_table.Length - 1));

    private readonly record struct Entry(bool Valid, uint Tag, uint Target, byte State);
}
