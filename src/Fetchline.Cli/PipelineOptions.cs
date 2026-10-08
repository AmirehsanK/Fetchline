using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Fetchline.Core.Pipeline;
using Fetchline.Viz;

namespace Fetchline.Cli;

/// <summary>
/// The what-if switches, as command-line options. Every command that runs the pipeline takes
/// the same ones, so they are declared once here. The names of their values are
/// <see cref="SwitchNames"/>, which the comparison table prints too.
/// </summary>
internal sealed class PipelineOptions
{
    private readonly Option<string> _hazards = new("--hazards")
    {
        Description = "How data hazards are handled: by forwarding, by stalling only, or not at all.",
    };

    private readonly Option<string> _branch = new("--branch")
    {
        Description = "The stage that decides branches: ex (two instructions squashed when taken) or id (one).",
    };

    private readonly Option<string> _predictor = new("--predictor")
    {
        Description = "How fetch guesses the way a branch will go: not-taken, backward-taken, 1-bit or 2-bit.",
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

    /// <param name="everyWay">
    /// The command tries every value of a switch that is not given, as <c>compare</c> does. The
    /// help then says so, where for a command that runs one configuration it names the default.
    /// </param>
    public PipelineOptions(bool everyWay = false)
    {
        _hazards.AcceptOnlyFromAmong(SwitchNames.All<HazardHandling>(SwitchNames.Of));
        _branch.AcceptOnlyFromAmong(SwitchNames.All<BranchDecision>(SwitchNames.Of));
        _predictor.AcceptOnlyFromAmong(SwitchNames.All<Predictor>(SwitchNames.Of));
        if (everyWay)
        {
            foreach (var option in new[] { _hazards, _branch, _predictor })
            {
                option.Description += " Every one is tried unless this is given.";
            }
        }
        else
        {
            _hazards.DefaultValueFactory = _ => SwitchNames.Of(PipelineConfig.Default.Hazards);
            _branch.DefaultValueFactory = _ => SwitchNames.Of(PipelineConfig.Default.Branches);
            _predictor.DefaultValueFactory = _ => SwitchNames.Of(PipelineConfig.Default.Predictor);
        }

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

    public void AddTo(Command command)
    {
        command.Add(_hazards);
        command.Add(_branch);
        command.Add(_predictor);
        command.Add(_btb);
        command.Add(_mulDiv);
    }

    /// <summary>The one configuration the switches describe; a switch that was not given has its default.</summary>
    public PipelineConfig Read(ParseResult parse)
    {
        var standard = PipelineConfig.Default;
        return new PipelineConfig
        {
            Hazards = parse.GetValue(_hazards) is { } hazards
                ? SwitchNames.Parse<HazardHandling>(hazards, SwitchNames.Of)
                : standard.Hazards,
            Branches = parse.GetValue(_branch) is { } branch
                ? SwitchNames.Parse<BranchDecision>(branch, SwitchNames.Of)
                : standard.Branches,
            Predictor = parse.GetValue(_predictor) is { } predictor
                ? SwitchNames.Parse<Predictor>(predictor, SwitchNames.Of)
                : standard.Predictor,
            BtbEntries = parse.GetValue(_btb),
            MulDivCycles = parse.GetValue(_mulDiv),
        };
    }

    /// <summary>
    /// The configurations a comparison covers. Hazard handling, the branch decision and the
    /// predictor each take every value they have, unless the switch was given, which fixes it.
    /// The other switches are the same throughout.
    /// </summary>
    public IReadOnlyList<PipelineConfig> ReadEvery(ParseResult parse)
    {
        var given = Read(parse);
        return Comparison.Configurations(
            given,
            IsGiven(parse, _hazards) ? [given.Hazards] : null,
            IsGiven(parse, _branch) ? [given.Branches] : null,
            IsGiven(parse, _predictor) ? [given.Predictor] : null);
    }

    private static bool IsGiven(ParseResult parse, Option option) => parse.GetResult(option) is { Implicit: false };

    /// <summary>
    /// The number an option was given, or null when what it was given is not a number. That
    /// case is the parser's to report: asking the result for its value here would throw.
    /// </summary>
    private static int? NumberOf(OptionResult result) =>
        result.Tokens.Count == 1
        && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
