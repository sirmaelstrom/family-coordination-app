namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Pure, DB-free due-determination helpers for the weekly digest (WP-05). Extracted from
/// <see cref="DigestService"/> so the day/hour/window logic — including the DST spring-forward guard —
/// is deterministically unit-testable WITHOUT a <c>DbContext</c> (the full <c>RunDueAsync</c>
/// orchestration is integration-tested in WP-08 against real Postgres, because EF InMemory does not
/// support the atomic <c>ExecuteUpdateAsync</c> claim).
/// <para>
/// All day-boundary math runs in the injected household timezone (M5/MN9) — never client/UTC date math,
/// never manual offset arithmetic. <c>now</c> is always the injected UTC instant (council — never
/// <see cref="DateTime.UtcNow"/>).
/// </para>
/// </summary>
internal static class DigestDue
{
    /// <summary>
    /// The UTC instant of the start of "today's send window" — local <paramref name="sendHour"/>:00 on the
    /// local calendar date of <paramref name="now"/>, converted back to UTC.
    /// <para>
    /// DST-safe: if local <paramref name="sendHour"/> lands in a spring-forward gap (an invalid local time,
    /// e.g. 02:00 on the jump day), it is pushed forward one hour past the missing hour — otherwise
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> would THROW
    /// <see cref="ArgumentException"/>. Fall-back (ambiguous) times resolve fine without intervention.
    /// </para>
    /// </summary>
    /// <param name="sendHour">The household's configured local send hour (0–23).</param>
    /// <param name="now">The current UTC instant (injected — never <see cref="DateTime.UtcNow"/>).</param>
    /// <param name="tz">The household timezone.</param>
    public static DateTime SendWindowStartUtc(int sendHour, DateTime now, TimeZoneInfo tz)
    {
        // ConvertTimeFromUtc yields Kind=Unspecified — required for the ConvertTimeToUtc round-trip below
        // (now.ToLocalTime() would give Kind.Local and throw). Mirror the calculator's date helpers.
        var asUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);

        var localSend = DateTime.SpecifyKind(localNow.Date.AddHours(sendHour), DateTimeKind.Unspecified);

        // DST spring-forward gap (e.g. 02:00 on the jump day): push past the missing hour so the convert
        // below does not throw. Without this guard a household with SendHourLocal in the gap aborts the run.
        if (tz.IsInvalidTime(localSend))
        {
            localSend = localSend.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localSend, tz); // ambiguous (fall-back) times resolve fine
    }

    /// <summary>
    /// True when the digest for a household configured with <paramref name="sendDay"/>/<paramref name="sendHour"/>
    /// is due as of <paramref name="now"/>: it is the right local weekday, the local hour has reached the send
    /// hour, AND it has not already been sent for today's window (<paramref name="lastSentAt"/> is null or
    /// strictly before <see cref="SendWindowStartUtc"/>). This is the idempotency guard (E10).
    /// </summary>
    /// <param name="sendDay">The household's configured local send weekday.</param>
    /// <param name="sendHour">The household's configured local send hour (0–23).</param>
    /// <param name="lastSentAt">UTC timestamp of the last successful send, or null if never sent.</param>
    /// <param name="now">The current UTC instant (injected — never <see cref="DateTime.UtcNow"/>).</param>
    /// <param name="tz">The household timezone.</param>
    public static bool IsDue(DayOfWeek sendDay, int sendHour, DateTime? lastSentAt, DateTime now, TimeZoneInfo tz)
    {
        var asUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);

        if (localNow.DayOfWeek != sendDay || localNow.Hour < sendHour)
        {
            return false;
        }

        var windowStart = SendWindowStartUtc(sendHour, now, tz);
        return lastSentAt is null || lastSentAt < windowStart;
    }
}
