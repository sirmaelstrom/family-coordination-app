using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Services.Dtos;

/// <summary>
/// The C (ledger) read-model for the chore-history surface (Phase 15). A completion <see cref="Events"/> feed,
/// the week scaffold (<see cref="Weeks"/>, one per week oldest→newest INCLUDING empty weeks — the client
/// derives per-day weave density by grouping <see cref="Events"/> by <c>localDate</c>), schedule-vs-completion
/// <see cref="Ghosts"/> (expected-but-missing beats), and the <see cref="GoneQuiet"/> band.
/// <para>
/// Household-scoped (M1 — the id comes from the resolved caller, never the client). <b>displayName only — no
/// userId / @mention field anywhere</b> (neutral framing, D9/MN1, contract-tested). Every date is a
/// server-stamped <c>yyyy-MM-dd</c> household-local string (MN9); the client groups by the string and never
/// builds a Date from it. Projected 1:1 from the shared <see cref="ChoreHistoryResult"/> (D3) — no recompute.
/// </para>
/// </summary>
public sealed record ChoreLedgerDto(
    string WindowStartLocal,
    string WindowEndLocal,
    IReadOnlyList<LedgerEventDto> Events,
    IReadOnlyList<LedgerWeekDto> Weeks,
    IReadOnlyList<GhostDto> Ghosts,
    IReadOnlyList<GoneQuietDto> GoneQuiet);

/// <summary>One completion in the feed. displayName only (no userId) — neutral framing (D9/MN1).</summary>
/// <param name="LocalDate">Household-local completion date, <c>yyyy-MM-dd</c> (MN9). Time-of-day, if shown,
/// derives client-side from the UTC instant — never parsed from this string.</param>
public sealed record LedgerEventDto(
    string ChoreName, string DoerDisplayName, string LocalDate, int Points, string? Note, bool HasPhoto);

/// <summary>One week of the weave scaffold. Per-day completion + ghost density derives client-side from
/// <see cref="ChoreLedgerDto.Events"/> / <see cref="ChoreLedgerDto.Ghosts"/> grouped within this week.</summary>
public sealed record LedgerWeekDto(string WeekStartLocal, int Completions);

/// <summary>An expected-but-missing beat. <c>Reason</c> is <c>"snoozed"</c> | <c>"slipped"</c>. The internal
/// <c>ReasonFromLog</c> telemetry flag is NOT on the wire (server-side only, D5).</summary>
public sealed record GhostDto(string ChoreName, string ExpectedLocalDate, string Reason);

/// <summary>
/// A chore that has gone quiet (≥2 trailing missed beats). <c>LastCompletedLocalDate</c> is JSON <c>null</c>
/// when the chore was never completed (the key is always present). <c>Reason</c> is <c>"snoozed"</c> |
/// <c>"slipped"</c>. <b>This is the single owner of the gone-quiet wire shape</b> — the recap payload (WP-05)
/// references THIS type; it is not redefined there.
/// </summary>
public sealed record GoneQuietDto(
    string ChoreName, string CadenceLabel, string? LastCompletedLocalDate, string Reason);

/// <summary>
/// Projects the shared <see cref="ChoreHistoryResult"/> onto the C (ledger) wire subset — dropping the
/// internal <c>ReasonFromLog</c> telemetry and the recap-only per-week distribution/points. Pure mapping, no
/// recompute (WP-04 boundary).
/// </summary>
public static class ChoreLedgerProjection
{
    public static ChoreLedgerDto ToLedger(ChoreHistoryResult r) => new(
        WindowStartLocal: r.WindowStartLocal,
        WindowEndLocal: r.WindowEndLocal,
        Events: r.Events
            .Select(e => new LedgerEventDto(e.ChoreName, e.DoerDisplayName, e.LocalDate, e.Points, e.Note, e.HasPhoto))
            .ToList(),
        Weeks: r.Weeks
            .Select(w => new LedgerWeekDto(w.WeekStartLocal, w.Completions))
            .ToList(),
        Ghosts: r.Ghosts
            .Select(g => new GhostDto(g.ChoreName, g.ExpectedLocalDate, g.Reason)) // ReasonFromLog dropped (D5)
            .ToList(),
        GoneQuiet: r.GoneQuiet
            .Select(q => new GoneQuietDto(q.ChoreName, q.CadenceLabel, q.LastCompletedLocalDate, q.Reason))
            .ToList());
}
