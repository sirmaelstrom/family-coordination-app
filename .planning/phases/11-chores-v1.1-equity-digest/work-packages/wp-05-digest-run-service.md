# WP-05: DigestService.RunDueAsync (orchestration)

**Wave:** 4 · **Execution:** review-needed · **Depends on:** WP-01, WP-02, WP-03, WP-04

## Precondition
Entity (WP-01), settings service incl. `GetDecryptedWebhookAsync` (WP-03), builder + `IDigestSender` (WP-04),
`ChoreEquityCalculator` (WP-02) all exist.

## Goal
A scoped `DigestService` that, given `now`, finds **due** households, builds each digest from its own data,
decrypts the webhook, sends, and stamps `LastSentAt` — idempotent and failure-isolated.

## Files
- **Create** `src/FamilyCoordinationApp/Services/Digest/IDigestService.cs`
- **Create** `src/FamilyCoordinationApp/Services/Digest/DigestService.cs`
- **Create** `src/FamilyCoordinationApp/Services/Digest/ChoreAttention.cs` — the **canonical attention
  predicates** shared by WP-05 (digest) and WP-06 (equity endpoint), so the lens and the digest tell the same
  story (council MAJOR — they were computed in two places):
  ```csharp
  internal static class ChoreAttention
  {
      public static bool IsFallingBehind(DueState d) => d is DueState.Overdue or DueState.DueToday;
      public static bool IsUpForGrabs(AssignmentKind kind, bool isClaimStale)
          => kind == AssignmentKind.None || isClaimStale;
  }
  ```
  (needs `using FamilyCoordinationApp.Services;` for `DueState` and `using FamilyCoordinationApp.Data.Entities;`
  for `AssignmentKind` — the file lives in `FamilyCoordinationApp.Services.Digest`.)
- Optional: put the `internal static DigestDue.IsDue(...)`/`SendWindowStartUtc(...)` pure helpers (see
  Verification) in `DigestService.cs` or a sibling `DigestDue.cs` — they're what the unit tests target.
- **Create** `tests/FamilyCoordinationApp.Tests/Services/DigestDueTests.cs` — pure unit tests of the
  due-determination + `sendWindowStartUtc` (DST guard) + `ChoreAttention` predicates (NO DbContext — see the
  InMemory/`ExecuteUpdateAsync` caveat in Verification). Full `RunDueAsync` is tested in WP-08 (real PG).

## Implementation notes
- Primary ctor `(IDbContextFactory<ApplicationDbContext> dbFactory, ChoreEquityCalculator equity,
  ChoreStatusCalculator status, DigestBuilder builder, IDigestSender sender, IDigestSettingsService settings,
  TimeZoneInfo tz, TimeProvider timeProvider, ILogger<DigestService> logger)`.
- `Task<DigestRunSummary> RunDueAsync(DateTime? now = null, CancellationToken ct = default)`:
  - `now ??= timeProvider.GetUtcNow().UtcDateTime`.
  - Query settings rows where `Enabled && WebhookUrlProtected != null`. Derive **once**
    `localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz)` (yields `DateTimeKind.Unspecified` — required for the
    `ConvertTimeToUtc` below; do NOT use `now.ToLocalTime()`, which gives `Kind.Local` and throws). For each
    household, compute its `sendWindowStartUtc` (per-household — `SendHourLocal` varies per row) and check:
    **due** iff `localNow.DayOfWeek == SendDayOfWeek && localNow.Hour >= SendHourLocal && (LastSentAt == null
    || LastSentAt < sendWindowStartUtc)` (E10). Skip not-due.
  - **Define `sendWindowStartUtc` precisely + DST-safe (P1 from review):**
    ```csharp
    var localSend = DateTime.SpecifyKind(localNow.Date.AddHours(s.SendHourLocal), DateTimeKind.Unspecified);
    if (tz.IsInvalidTime(localSend))            // DST spring-forward gap (e.g. 02:00 on the jump day)
        localSend = localSend.AddHours(1);      // push past the missing hour; ConvertTimeToUtc would otherwise THROW
    var sendWindowStartUtc = TimeZoneInfo.ConvertTimeToUtc(localSend, tz); // ambiguous (fall-back) times resolve fine
    ```
    So "haven't sent in today's window yet" ⇔ `LastSentAt < sendWindowStartUtc` (NOT local midnight — local
    `SendHourLocal` of today). **`tz.IsInvalidTime` guard is mandatory** — without it a household with
    `SendHourLocal` in the spring-forward gap throws `ArgumentException` and (since this is computed in the
    selection loop) could abort the whole run. Also wrap each household's processing in try/catch (M10) so even
    an unexpected throw is isolated. Never manual offset math (MN9-spirit).
  - **Concurrency-safe atomic claim BEFORE sending (council C2 — read-send-stamp is NOT safe under two
    concurrent cron hits; both could pass the guard and double-post):** for each due household, claim it with a
    single conditional UPDATE and check the affected-row count —
    ```csharp
    var claimed = await ctx.ChoreDigestSettings
        .Where(s => s.HouseholdId == h && s.Enabled && s.WebhookUrlProtected != null
                    && (s.LastSentAt == null || s.LastSentAt < sendWindowStartUtc))
        .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSentAt, now), ct);   // atomic at the DB
    if (claimed != 1) continue;   // another run already claimed this household's window
    ```
    Only the run whose UPDATE matched (exactly one, enforced by Postgres row-locking) proceeds to build+send.
    `ExecuteUpdateAsync` (EF 10) is a direct atomic SQL UPDATE — no change-tracker race.
  - Then (the claimer only): fetch members (`MemberDto`s) + this-week `ChoreCompletion`s + active chores
    **scoped to that household** (M1); `equity.Compute(...)`; build `IReadOnlyList<DigestChoreLine>` from the
    active chores via `ChoreStatusCalculator.Compute` + `ChoreAttention` (falling-behind set); compute
    `upForGrabsCount` via `ChoreAttention.IsUpForGrabs`; `builder.Build(householdName, equityResult,
    choreDueness, upForGrabsCount)`; `var url = await settings.GetDecryptedWebhookAsync(h, ct)`.
    **Null-webhook handling (council MAJOR):** if `url is null` (unprotect failed / cleared between select and
    claim), **do not call the sender**; **compensate** (restore the prior `LastSentAt` so it isn't falsely
    marked sent) and count as `Failed`; log a warning WITHOUT the value. Else `await sender.SendAsync(url,
    model, ct)`.
  - **On send failure (exception):** catch per-household (M10); **compensate** — restore the claimed row's
    `LastSentAt` to its prior value (so a later hourly tick retries this window); count `Failed`; log without
    the URL; continue. (Capture `priorLastSentAt` before the claim UPDATE.)
  - Return `DigestRunSummary { Sent, Skipped, Failed }` (no URLs). **`Skipped` semantics (council MAJOR):**
    count households that were enabled + configured but **not due** (day/hour/window). Disabled/unconfigured
    rows are filtered out of the candidate query and are **not** counted (document this in the summary's XML
    doc so WP-08 asserts the right numbers). `Sent` = successful sends; `Failed` = claimed-but-send/decrypt-failed.
- Use short-lived contexts per op (M2). The active-chore dueness uses `ChoreStatusCalculator.Compute` +
  `ChoreAttention` (the same predicates WP-06's equity endpoint uses — no divergence).

## Verification (V7 unit portion)
- **⚠ InMemory does NOT support `ExecuteUpdateAsync`** (it throws `InvalidOperationException` — final-validation
  P1). So `RunDueAsync` **cannot be exercised end-to-end against the InMemory provider**. Therefore:
  - **Unit-test the PURE pieces** (no DB): extract due-determination + `sendWindowStartUtc` (incl. the DST
    `IsInvalidTime` guard) into an `internal static` helper (e.g. `DigestDue.IsDue(settingsSnapshot, now, tz)`
    + `DigestDue.SendWindowStartUtc(...)`) and unit-test those directly (frozen now+tz, DST spring-forward
    case, day/hour boundaries, `LastSentAt` window). Unit-test `ChoreAttention.IsFallingBehind/IsUpForGrabs`
    truth tables. These need no DbContext and are the high-value deterministic checks.
  - **The full `RunDueAsync` orchestration** (atomic `ExecuteUpdateAsync` claim, idempotency, failure
    isolation, multi-tenant) is **integration-only (WP-08, real Postgres)** — do NOT write an InMemory test
    that calls `RunDueAsync`, it would throw on the claim. (`FakeDigestSender` + ephemeral DataProtection are
    used by WP-08's host, not an InMemory unit test here.)
- Unit suite green (`--filter "kind!=integration"`), no Docker; format clean.

## Failure criteria
- Sends twice within a window (idempotency broken). · A failing household aborts the whole run. · Cross-
  household data bleed into a digest. · Webhook value in logs/summary. · Uses `DateTime.UtcNow` instead of the
  injected `TimeProvider`/`now`.

## Boundary
Orchestration service + tests ONLY. No endpoint (WP-06), no `Program.cs`, no `BackgroundService` (MN1 — firing
is the WP-06 endpoint + external cron). Does not define the trigger-token auth (WP-06).

## Notes for downstream
- WP-06 maps `POST /api/chores/digest/run` (token-gated) → `IDigestService.RunDueAsync()` and returns the
  summary as JSON (counts only). WP-08 drives the endpoint twice against real Postgres + `FakeDigestSender`.
