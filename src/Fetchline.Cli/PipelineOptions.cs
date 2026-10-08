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

    public PipelineOptions()
    {
        _hazards.AcceptOnlyFromAmong("forwarding", "stall", "off");
        _branch.AcceptOnlyFromAmong("ex", "id");
    }

    public void AddTo(Command command)
    {
        command.Add(_hazards);
        command.Add(_branch);
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
    };
}
