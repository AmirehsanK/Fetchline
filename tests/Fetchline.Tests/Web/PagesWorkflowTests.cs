namespace Fetchline.Tests.Web;

/// <summary>
/// The workflow that publishes the playground. It cannot be run from here, so what can be read
/// off it is: that it waits to be started, and that the line it rewrites is in the page.
/// </summary>
public class PagesWorkflowTests
{
    private static readonly string Workflow = File.ReadAllText(Repo.PathOf(".github", "workflows", "pages.yml"));

    [Fact]
    public void ItRunsOnlyWhenSomeoneStartsIt()
    {
        // Publishing is the owner's decision: until Pages is switched on, a run on every push
        // would only be a failure on every push.
        var triggers = Workflow[Workflow.IndexOf("\non:\n", StringComparison.Ordinal)..Workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];
        var live = triggers.Split('\n').Where(line => line.Length > 0 && !line.TrimStart().StartsWith('#')).ToArray();

        Assert.Equal(["on:", "  workflow_dispatch:"], live);
    }

    [Fact]
    public void TheLineItRewritesIsTheOneInThePage()
    {
        const string Base = "<base href=\"/\" />";
        var page = File.ReadAllText(Repo.PathOf("src", "Fetchline.Web", "wwwroot", "index.html"));

        Assert.Contains(Base, page);
        Assert.Contains($"s|{Base}|", Workflow);

        // Everything else the page asks for is asked for relative to that line.
        Assert.DoesNotContain("href=\"/", page.Replace(Base, string.Empty));
        Assert.DoesNotContain("src=\"/", page);
    }

    [Fact]
    public void NothingIsPublishedThatHasNotPassedItsTests()
    {
        var test = Workflow.IndexOf("dotnet test", StringComparison.Ordinal);
        var publish = Workflow.IndexOf("dotnet publish", StringComparison.Ordinal);

        Assert.True(test > 0 && publish > test);
    }
}
