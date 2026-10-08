using Fetchline.Viz;

namespace Fetchline.Tests.Viz;

/// <summary>The examples the playground offers: the files under <c>examples/</c>, in a chosen order.</summary>
public class ExampleCatalogTests
{
    [Fact]
    public void TheCatalogueIsTheFilesInTheExamplesFolderAndNothingElse()
    {
        var files = Directory.GetFiles(Repo.PathOf("examples"), "*.s")
            .ToDictionary(path => Path.GetFileName(path), path => File.ReadAllText(path).ReplaceLineEndings("\n"));

        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), ExampleCatalog.All.Select(example => example.Name).Order(StringComparer.Ordinal));
        Assert.All(ExampleCatalog.All, example => Assert.Equal(files[example.Name], example.Source));
    }

    [Fact]
    public void TheMenuGoesFromTheFourLessonsToWholeProgramsShortestFirst()
    {
        // A new example has to be given its place here and in the catalogue: left to itself it
        // would go to the end, which is rarely where it belongs.
        Assert.Equal(
            [
                "load-use.s", "forwarding.s", "branch.s", "multiply.s",
                "hello.s", "sum.s", "fib.s", "gcd.s", "factorial.s", "bubble-sort.s", "primes.s",
            ],
            ExampleCatalog.All.Select(example => example.Name));
        Assert.Equal("load-use.s", ExampleCatalog.First.Name);
    }

    [Fact]
    public void EveryExampleSaysWhatItIsInOneSentence()
    {
        var summaries = ExampleCatalog.All.ToDictionary(example => example.Name, example => example.Summary);

        Assert.Equal("The load-use hazard, as the textbooks draw it", summaries["load-use.s"]);
        Assert.Equal("The textbook forwarding sequence", summaries["forwarding.s"]);
        Assert.Equal("A branch that is not taken costs nothing", summaries["branch.s"]);
        Assert.Equal("What a slow multiplier costs", summaries["multiply.s"]);
        Assert.Equal("Prints a greeting, then exits", summaries["hello.s"]);
        Assert.Equal("Adds the numbers 1 to 10 and prints the total: 55", summaries["sum.s"]);
        Assert.Equal("Counts the primes below 10000 with the sieve of Eratosthenes: 1229", summaries["primes.s"]);

        // Short enough for a menu, and a sentence: something, with no full stop inside or after.
        Assert.All(summaries.Values, summary =>
        {
            Assert.InRange(summary.Length, 10, 80);
            Assert.DoesNotContain(". ", summary);
            Assert.False(summary.EndsWith('.'));
        });
    }

    [Theory]
    [InlineData("# One thing.\nnop", "One thing")]
    [InlineData("# One thing that runs\n# over two lines. And more.\nnop", "One thing that runs over two lines")]
    [InlineData("# The first paragraph\n#\n# The second.\nnop", "The first paragraph")]
    [InlineData("#No space after the mark\nnop", "No space after the mark")]
    [InlineData("  # Indented all the same.\nnop", "Indented all the same")]
    [InlineData("nop   # a comment, but not an opening one", "")]
    [InlineData("\n# A blank line first is not an opening comment either\nnop", "")]
    [InlineData("", "")]
    public void TheSummaryIsTheFirstSentenceOfTheOpeningComment(string source, string summary)
    {
        Assert.Equal(summary, ExampleCatalog.SummaryOf(source));
    }

    [Fact]
    public void AnExampleIsFoundByItsNameOrNotAtAll()
    {
        Assert.Equal("fib.s", ExampleCatalog.Find("fib.s")!.Name);
        Assert.StartsWith("# Prints the first ten Fibonacci numbers", ExampleCatalog.Find("fib.s")!.Source);
        Assert.Null(ExampleCatalog.Find("fib"));
        Assert.Null(ExampleCatalog.Find(string.Empty));
    }
}
