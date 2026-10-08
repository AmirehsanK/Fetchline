using Fetchline.Core.Pipeline;
using Fetchline.Viz;
using Fetchline.Viz.Explain;

namespace Fetchline.Tests.Viz;

/// <summary>The status line's account of a run, and the paces a run can go at.</summary>
public class StandingTests
{
    private static readonly IMessages Messages = EnglishMessages.Instance;

    private static string Example(string name) => File.ReadAllText(Repo.PathOf("examples", name));

    private static string At(Session session, ulong cycle, bool running = false)
    {
        session.Seek(cycle);
        return Standing.Of(session, running, Messages);
    }

    [Fact]
    public void ARunIsReadyThenAtACycleThenEnded()
    {
        var session = new Session(Example("load-use.s"));

        Assert.Equal("READY", At(session, 0));
        Assert.Equal("CYCLE 3", At(session, 3));
        Assert.Equal("CYCLE 3  RUNNING", At(session, 3, running: true));
        Assert.Equal("CYCLE 8  ENDED", At(session, ulong.MaxValue));

        // Stepped back from the end, it is at a cycle again: the end is the last cycle's news.
        Assert.Equal("CYCLE 5", At(session, 5));
        Assert.Equal("READY", At(session, 0));
    }

    [Theory]
    [InlineData("li a7, 10\necall", "CYCLE 6  EXIT 0")]
    [InlineData("li a0, 3\nli a7, 93\necall", "CYCLE 7  EXIT 3")]
    [InlineData("li a0, 1\nebreak\nli a0, 2", "CYCLE 6  PAUSED AT EBREAK")]
    [InlineData(
        "li a0, 5\nsw a0, 0(zero)",
        "CYCLE 6  STOPPED: a store to 0x00000000, which is in 'text' and cannot be written, at pc 0x00000004")]
    public void HowAProgramStoppedIsSaid(string source, string standing)
    {
        var session = new Session(source);
        session.Run(1000);

        Assert.Equal(standing, Standing.Of(session, running: false, Messages));
    }

    [Fact]
    public void ASourceThatDoesNotAssembleHasNothingToRun()
    {
        Assert.Equal("NOTHING TO RUN", Standing.Of(new Session("adid a0, a0, 1"), running: false, Messages));
        Assert.Equal("NOTHING TO RUN", Standing.Of(new Session("adid a0, a0, 1"), running: true, Messages));
    }

    [Fact]
    public void ARunThatHasGoneWrongSaysSoFromTheCycleItDid()
    {
        var session = new Session(Example("sum.s"), new PipelineConfig { Hazards = HazardHandling.Off });

        Assert.Equal("CYCLE 7", At(session, 7));
        Assert.Equal("CYCLE 8  WRONG SINCE CYCLE 8", At(session, 8));
        Assert.Equal("CYCLE 20  RUNNING  WRONG SINCE CYCLE 8", At(session, 20, running: true));
        Assert.Equal("CYCLE 73  EXIT 0  WRONG SINCE CYCLE 8", At(session, ulong.MaxValue));

        // Before the mistake the run looks as it did then.
        Assert.Equal("CYCLE 4", At(session, 4));
    }

    [Fact]
    public void TheWordsOfTheStatusLineAndTheKeysAreInTheCatalog()
    {
        Assert.Equal(
            ["RUN", "PAUSE", "STEP", "BACK", "RESET"],
            new[] { Messages.RunKey, Messages.PauseKey, Messages.StepKey, Messages.BackKey, Messages.ResetKey });
        Assert.Equal("SPEED 4/s", Messages.SpeedKey("4/s"));
        Assert.Equal("SPEED MAX", Messages.SpeedKey(string.Empty));
        Assert.Equal("CYCLE 12345", Messages.StatusCycle(12_345));
    }

    [Fact]
    public void ThePacesGoFromOneCycleASecondToFlatOutAndRoundAgain()
    {
        var paces = RunPace.All;

        Assert.Equal(["1/s", "4/s", "16/s", string.Empty], paces.Select(pace => pace.Name));
        Assert.Equal(RunPace.All[1], RunPace.Default);
        Assert.Equal([false, false, false, true], paces.Select(pace => pace.IsFlatOut));

        // A named pace is one cycle at a time, at the rate its name says, near enough.
        foreach (var pace in paces.Where(pace => !pace.IsFlatOut))
        {
            var rate = int.Parse(pace.Name.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1, pace.Cycles);
            Assert.InRange(1000.0 / pace.Milliseconds, rate * 0.95, rate * 1.05);
        }

        // Flat out does many cycles between pauses, and still pauses: the page has to be drawn.
        Assert.True(paces[^1].Cycles >= 1000 && paces[^1].Milliseconds > 0);

        Assert.Equal(paces[2], paces[1].Next());
        Assert.Equal(paces[0], paces[^1].Next());
        Assert.Equal(RunPace.Default, new RunPace("7/s", 1, 143).Next());
    }
}
