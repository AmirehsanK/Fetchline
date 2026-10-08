using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
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

    private readonly Option<int> _mulDiv = new("--muldiv")
    {
        Description = "The number of cycles a multiply or a divide spends in EX.",
        DefaultValueFactory = _ => PipelineConfig.Default.MulDivCycles,
    };

    public PipelineOptions()
    {
        _hazards.AcceptOnlyFromAmong("forwarding", "stall", "off");
        _branch.AcceptOnlyFromAmong("ex", "id");
        _predictor.AcceptOnlyFromAmong("not-taken", "backward-taken", "1-bit", "2-bit");
        _btb.Validators.Add(result =>
        {
            if (NumberOf(result) is { } entries && (entries is < 1 or > 65536 || (entries & (entries - 1)) != 0))
            {
                result.AddError($"--btb takes a power of two up to 65536, not {entries}.");
            }
        });
        _mulDiv.Validators.Add(result =>
        {
            if (NumberOf(result) is { } cycles && cycles is < 1 or > PipelineConfig.MaxMulDivCycles)
            {
                result.AddError($"--muldiv takes a number of cycles from 1 to {PipelineConfig.MaxMulDivCycles}, not {cycles}.");
            }
        });
    }

    /// <summary>
    /// The number an option was given, or null when what it was given is not a number. That
    /// case is the parser's to report: asking the result for its value here would throw.
    /// </summary>
    private static int? NumberOf(OptionResult result) =>
        result.Tokens.Count == 1
        && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    public void AddTo(Command command)
    {
        command.Add(_hazards);
        command.Add(_branch);
        command.Add(_predictor);
        command.Add(_btb);
        command.Add(_mulDiv);
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
        MulDivCycles = parse.GetValue(_mulDiv),
    };
}
