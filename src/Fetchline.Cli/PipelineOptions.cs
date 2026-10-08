using System.CommandLine;
using Fetchline.Core.Pipeline;

namespace Fetchline.Cli;

/// <summary>
/// The what-if switches, as command-line options. Every command that runs the pipeline takes
/// the same ones, so they are declared once here.
/// </summary>
internal sealed class PipelineOptions
{
    private readonly Option<string> _hazards = new("--hazards")
    {
        Description = "How data hazards are handled: by forwarding, by stalling only, or not at all.",
        DefaultValueFactory = _ => "forwarding",
    };

    private readonly Option<string> _branch = new("--branch")
    {
        Description = "The stage that decides branches: ex (two instructions squashed when taken) or id (one).",
        DefaultValueFactory = _ => "ex",
    };

    private readonly Option<string> _predictor = new("--predictor")
    {
        Description = "How fetch guesses the way a branch will go: not-taken, backward-taken, 1-bit or 2-bit.",
        DefaultValueFactory = _ => "not-taken",
    };

    private readonly Option<int> _btb = new("--btb")
    {
        Description = "The number of entries in the branch target buffer of a 1-bit or 2-bit predictor.",
        DefaultValueFactory = _ => PipelineConfig.Default.BtbEntries,
    };

    public PipelineOptions()
    {
        _hazards.AcceptOnlyFromAmong("forwarding", "stall", "off");
        _branch.AcceptOnlyFromAmong("ex", "id");
        _predictor.AcceptOnlyFromAmong("not-taken", "backward-taken", "1-bit", "2-bit");
        _btb.Validators.Add(result =>
        {
            var entries = result.GetValueOrDefault<int>();
            if (entries is < 1 or > 65536 || (entries & (entries - 1)) != 0)
            {
                result.AddError($"--btb takes a power of two up to 65536, not {entries}.");
            }
        });
    }

    public void AddTo(Command command)
    {
        command.Add(_hazards);
        command.Add(_branch);
        command.Add(_predictor);
        command.Add(_btb);
    }

    public PipelineConfig Read(ParseResult parse) => new()
    {
        Hazards = parse.GetValue(_hazards) switch
        {
            "stall" => HazardHandling.StallOnly,
            "off" => HazardHandling.Off,
            _ => HazardHandling.Forwarding,
        },
        Branches = parse.GetValue(_branch) == "id" ? BranchDecision.Decode : BranchDecision.Execute,
        Predictor = parse.GetValue(_predictor) switch
        {
            "backward-taken" => Predictor.BackwardTaken,
            "1-bit" => Predictor.OneBit,
            "2-bit" => Predictor.TwoBit,
            _ => Predictor.NotTaken,
        },
        BtbEntries = parse.GetValue(_btb),
    };
}
