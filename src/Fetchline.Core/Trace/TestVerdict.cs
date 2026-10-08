using System.Globalization;

namespace Fetchline.Core.Trace;

/// <summary>
/// What a run of an official test program came to. A test ends by writing a word to
/// <c>tohost</c>: 1 is a pass, <c>(n &lt;&lt; 1) | 1</c> says test case <c>n</c> failed, and a word
/// with 1337 OR'ed into it says an exception arrived that the test had no handler for.
/// </summary>
public readonly record struct TestVerdict(bool Passed, string Summary)
{
    private const int UnexpectedException = 1337;

    public static TestVerdict Of(in Commit last) => last.Stop switch
    {
        StopReason.Tohost when last.ExitCode == 1 => new TestVerdict(true, "pass"),
        StopReason.Tohost when (last.ExitCode & UnexpectedException) == UnexpectedException =>
            new TestVerdict(false, Text($"an exception the test did not expect (tohost = {last.ExitCode})")),
        StopReason.Tohost => new TestVerdict(false, Text($"test case {(uint)last.ExitCode >> 1} failed")),
        StopReason.None => new TestVerdict(false, "did not finish"),
        StopReason.Fault => new TestVerdict(false, last.Message ?? "stopped by a fault"),
        _ => new TestVerdict(false, Text($"stopped without a verdict ({last.Stop})")),
    };

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
