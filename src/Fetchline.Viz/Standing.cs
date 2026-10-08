using Fetchline.Core.Trace;
using Fetchline.Viz.Explain;

namespace Fetchline.Viz;

/// <summary>
/// The status line's account of a run: which cycle is on screen, and how things stand. It says
/// what a reader would otherwise have to work out from the panes: that the program has ended and
/// how, that it is paused at an <c>ebreak</c>, that it stopped and why, that it is going.
/// </summary>
public static class Standing
{
    /// <param name="running">The run is going on its own, cycle after cycle.</param>
    public static string Of(Session session, bool running, IMessages messages)
    {
        if (session.Program is null)
        {
            return messages.StatusNothingToRun;
        }

        var cycle = messages.StatusCycle(session.Cycle);
        var last = session.Record?.End ?? session.Record?.Commit;
        var standing = session.Stopped switch
        {
            StopReason.Fault => $"{cycle}  {messages.StatusStopped(last?.Message ?? string.Empty)}",
            StopReason.Breakpoint => $"{cycle}  {messages.StatusPaused}",
            StopReason.Exit => $"{cycle}  {messages.StatusExited(last?.ExitCode ?? 0)}",
            StopReason.None when running => $"{cycle}  {messages.StatusRunning}",
            StopReason.None when session.Cycle == 0 => messages.StatusReady,
            StopReason.None => cycle,
            _ => $"{cycle}  {messages.StatusEnded}",
        };

        // A run that has gone wrong says so from the cycle it did, whatever else is true of it.
        return session.Divergence is { } divergence ? $"{standing}  {messages.StatusWrong(divergence.Cycle)}" : standing;
    }
}
