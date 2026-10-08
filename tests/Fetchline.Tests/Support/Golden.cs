namespace Fetchline.Tests.Support;

/// <summary>
/// Text kept under <c>tests/golden</c> and compared exactly. When a deliberate change alters it,
/// run the tests once with the environment variable <c>FETCHLINE_UPDATE_GOLDEN</c> set to 1, then
/// read the files that changed before committing them: they are the picture the tool draws.
/// </summary>
internal static class Golden
{
    public static void Check(string name, string actual)
    {
        var path = Repo.PathOf("tests", "golden", name);
        if (Environment.GetEnvironmentVariable("FETCHLINE_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"there is no golden file '{name}'; run once with FETCHLINE_UPDATE_GOLDEN=1 and review it");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
