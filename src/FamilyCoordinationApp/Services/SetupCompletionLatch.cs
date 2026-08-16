namespace FamilyCoordinationApp.Services;

/// <summary>
/// One-way, per-process memo of "a household exists". Singleton: <see cref="SetupService"/> is scoped, so the
/// latch cannot live on it.
/// </summary>
/// <remarks>
/// It latches only TRUE, and only after a query observed a household — the app has no household-deletion path, so
/// the observation cannot become false while the process lives. Restarting clears it, which is the recovery if a
/// household is ever removed out of band.
/// </remarks>
public sealed class SetupCompletionLatch
{
    private volatile bool _complete;

    public bool IsComplete => _complete;

    public void MarkComplete() => _complete = true;
}
