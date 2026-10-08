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

    public PipelineOptions()
    {
        _hazards.AcceptOnlyFromAmong("forwarding", "stall", "off");
    }

    public void AddTo(Command command) => command.Add(_hazards);

    public PipelineConfig Read(ParseResult parse) => new()
    {
        Hazards = parse.GetValue(_hazards) switch
        {
            "stall" => HazardHandling.StallOnly,
            "off" => HazardHandling.Off,
            _ => HazardHandling.Forwarding,
        },
    };
}
