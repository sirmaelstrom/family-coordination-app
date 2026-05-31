namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for unit tests — <see cref="GetUtcNow"/> returns a fixed
/// instant. Avoids a dependency on the <c>Microsoft.Extensions.TimeProvider.Testing</c> package while giving
/// <see cref="FamilyCoordinationApp.Services.ChoreService"/> a frozen clock (no <c>DateTime.UtcNow</c>).
/// </summary>
public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTimeOffset _now = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Advance (or set) the clock; handy for multi-step temporal tests.</summary>
    public void SetUtcNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
