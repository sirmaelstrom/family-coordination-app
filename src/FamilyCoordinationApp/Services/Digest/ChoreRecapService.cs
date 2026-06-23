using System.Globalization;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Read-only sibling of <see cref="DigestService"/> for the IN-APP weekly recap lens. It builds the SAME
/// current-week model the Discord digest sends (reusing <see cref="DigestBuilder"/> + the shared
/// <see cref="ChoreEquityCalculator"/> / <see cref="ChoreStatusCalculator"/> / <see cref="ChoreAttention"/>
/// pieces, so the in-app view and the channel post never diverge), and adds the week-over-week trend the
/// digest can't show.
/// <para>
/// Fully household-scoped (M1 — the id comes from the resolved caller, never the client) and read-only: it
/// never sends, never touches a webhook, never stamps <c>LastSentAt</c>. A short-lived context per call (M2).
/// </para>
/// <para>
/// Trend windowing reuses <see cref="ChoreEquityCalculator.WeekStartUtc"/>: each past week is
/// <c>[WeekStartUtc(now − 7k), WeekStartUtc(now − 7(k−1)))</c>, so a completion lands in exactly one week and
/// the boundaries are computed in the household-local Monday-start calendar (DST-correct, no manual offset
/// math). The current week (k = 0) uses the same lower bound as the equity <see cref="EquityWindow.Week"/>
/// path, so its totals match the digest exactly.
/// </para>
/// </summary>
public interface IChoreRecapService
{
    /// <summary>
    /// Build the recap for <paramref name="householdId"/>: the current week's assembled digest content plus a
    /// <paramref name="weeks"/>-long week-over-week trend (clamped 1–26), oldest→newest.
    /// </summary>
    /// <param name="householdId">Resolved caller's household (M1 — never a client-supplied id).</param>
    /// <param name="weeks">How many weeks of trend to return (including the current week). Clamped 1–26.</param>
    /// <param name="now">UTC instant to evaluate against; defaults to the injected <c>TimeProvider</c> now.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ChoreRecapDto> GetRecapAsync(int householdId, int weeks, DateTime? now = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IChoreRecapService"/>
public class ChoreRecapService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreEquityCalculator equity,
    ChoreStatusCalculator status,
    DigestBuilder builder,
    TimeZoneInfo tz,
    TimeProvider timeProvider) : IChoreRecapService
{
    private const int MinWeeks = 1;
    private const int MaxWeeks = 26;

    /// <inheritdoc />
    public async Task<ChoreRecapDto> GetRecapAsync(
        int householdId, int weeks, DateTime? now = null, CancellationToken ct = default)
    {
        var asOf = DateTime.SpecifyKind(now ?? timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        weeks = Math.Clamp(weeks, MinWeeks, MaxWeeks);

        // The recap only needs completions back to the oldest trend week — bound the scan to that window so a
        // long-lived household reads O(weeks of data), not O(all history). The current-week equity compute
        // filters to this week itself, which is inside this bound. All reads are AsNoTracking (read-only path).
        var oldestWeekStartUtc = ChoreEquityCalculator.WeekStartUtc(asOf.AddDays(-7 * (weeks - 1)), tz);

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // Mirror DigestService.BuildModelAsync's read shape — all queries scoped to householdId (M1).
        var householdName = await context.Households
            .AsNoTracking()
            .Where(h => h.Id == householdId)
            .Select(h => h.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var members = await context.Users
            .AsNoTracking()
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl))
            .ToListAsync(ct);

        var completions = await context.ChoreCompletions
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.CompletedAt >= oldestWeekStartUtc)
            .ToListAsync(ct);

        var activeChores = await context.Chores
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        var current = BuildCurrentWeek(householdName, members, completions, activeChores, asOf);
        var trend = BuildTrend(completions, asOf, weeks);

        return new ChoreRecapDto(current, trend);
    }

    /// <summary>
    /// Assemble the current week EXACTLY as <see cref="DigestService.BuildModelAsync"/> does (same equity
    /// compute, same snoozed-exclusion dueness loop, same <see cref="DigestBuilder"/>) so the in-app recap is
    /// byte-for-byte the digest the channel receives, then project it to the wire DTO.
    /// </summary>
    private RecapWeekDto BuildCurrentWeek(
        string householdName,
        IReadOnlyList<MemberDto> members,
        IReadOnlyList<ChoreCompletion> completions,
        IReadOnlyList<Chore> activeChores,
        DateTime now)
    {
        var equityResult = equity.Compute(completions, members, EquityWindow.Week, now, tz);

        var choreDueness = new List<DigestChoreLine>(activeChores.Count);
        var upForGrabsCount = 0;
        foreach (var chore in activeChores)
        {
            var dueness = status.Compute(ChoreRecurrenceSnapshot.FromChore(chore), now, tz);

            // Snoozed chores carry no pressure (WP-04) — excluded from BOTH the falling-behind line list and
            // the up-for-grabs count, exactly as the digest does.
            if (dueness.IsSnoozed)
            {
                continue;
            }

            choreDueness.Add(new DigestChoreLine(chore.Name, dueness.DueState));

            var isClaimStale = status.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now);
            if (ChoreAttention.IsUpForGrabs(chore.AssignmentKind, isClaimStale))
            {
                upForGrabsCount++;
            }
        }

        var model = builder.Build(householdName, equityResult, choreDueness, upForGrabsCount);

        return new RecapWeekDto(
            WeekStartLocal: LocalIsoDate(ChoreEquityCalculator.WeekStartUtc(now, tz)),
            Headline: model.CollectiveHeadline,
            TotalCompletions: model.TotalCompletions,
            TotalPoints: model.TotalPoints,
            Distribution: model.Distribution
                .Select(m => new RecapMemberLineDto(m.DisplayName, m.Points, m.SharePct))
                .ToList(),
            FallingBehind: model.FallingBehind,
            UpForGrabsCount: model.UpForGrabsCount);
    }

    /// <summary>
    /// Bucket the household's completions into the last <paramref name="weeks"/> local weeks (oldest→newest;
    /// the final point, k = 0, is the in-progress current week). Each week is
    /// <c>[WeekStartUtc(now − 7k), WeekStartUtc(now − 7(k−1)))</c> — half-open so a Sunday-23:59 completion and
    /// the following Monday-00:00 completion land in different weeks.
    /// </summary>
    private IReadOnlyList<RecapTrendPointDto> BuildTrend(
        IReadOnlyList<ChoreCompletion> completions, DateTime now, int weeks)
    {
        // Bucket every completion ONCE by its local week-start (O(completions)), then emit the requested
        // weeks from the buckets (O(weeks)) — instead of rescanning all completions per week. Both sides
        // key on the SAME WeekStartUtc, so a completion lands in exactly the week whose boundary it matches;
        // completions outside the requested range bucket into a key that's never emitted.
        var byWeekStart = new Dictionary<DateTime, (int Completions, int Points)>();
        foreach (var c in completions)
        {
            var at = DateTime.SpecifyKind(c.CompletedAt, DateTimeKind.Utc);
            var weekStart = ChoreEquityCalculator.WeekStartUtc(at, tz);
            byWeekStart.TryGetValue(weekStart, out var agg);
            byWeekStart[weekStart] = (agg.Completions + 1, agg.Points + c.EffortPointsSnapshot);
        }

        var points = new List<RecapTrendPointDto>(weeks);
        // Oldest → newest; the last point (k == 0) is the in-progress current week.
        for (var k = weeks - 1; k >= 0; k--)
        {
            var weekStartUtc = ChoreEquityCalculator.WeekStartUtc(now.AddDays(-7 * k), tz);
            byWeekStart.TryGetValue(weekStartUtc, out var agg);

            points.Add(new RecapTrendPointDto(
                WeekStartLocal: LocalIsoDate(weekStartUtc),
                TotalCompletions: agg.Completions,
                TotalPoints: agg.Points,
                IsCurrent: k == 0));
        }
        return points;
    }

    /// <summary>Format a UTC week-start as a household-local ISO date (<c>yyyy-MM-dd</c>) — server-side so the
    /// island never builds a Date from a date-only string (MN9).</summary>
    private string LocalIsoDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
