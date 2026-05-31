# WP-02: Equity calculator + DTO + contract fixture

**Wave:** 2 · **Execution:** autonomous · **Depends on:** — (existing `ChoreCompletion`, `ChoreStatusCalculator`)

## Precondition
`ChoreCompletion` carries `CompletedByUserId`, `CompletedAt` (UTC), `EffortPointsSnapshot`
(`Data/Entities/ChoreCompletion.cs`). `ChoreStatusCalculator` is a pure, stateless, parameterless-ctor
service taking `now`+`TimeZoneInfo` as method params (`Services/ChoreStatusCalculator.cs`).

## Goal
A pure `ChoreEquityCalculator` that aggregates the completion log into a per-member effort-weighted
distribution over a window; the frozen `ChoreEquityDto`; and its contract fixture.

## Files
- **Create** `src/FamilyCoordinationApp/Services/ChoreEquityCalculator.cs` (pure, stateless)
- **Create** `src/FamilyCoordinationApp/Services/Dtos/ChoreEquityDtos.cs` (`ChoreEquityDto`, `MemberShareDto`, the `EquityWindow` enum or a validated string)
- **Create** `tests/FamilyCoordinationApp.Tests/Services/ChoreEquityCalculatorTests.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Fixtures/ChoreEquity/equity.json`
- **Create** `tests/FamilyCoordinationApp.Tests/Services/ChoreEquityDtoContractTests.cs`

## Implementation notes
- `ChoreEquityCalculator` (namespace `FamilyCoordinationApp.Services`): a method
  `ChoreEquityResult Compute(IEnumerable<ChoreCompletion> completions, IReadOnlyList<MemberDto> members,
  EquityWindow window, DateTime now, TimeZoneInfo tz)`. **Reuse the existing `MemberDto`** (`Services/Dtos/
  ChoreDtos.cs` — `userId/displayName/initials/pictureUrl`) as the member input; do **not** invent a new
  `MemberRef` type.
- **FREEZE the internal result type here (council C1 — it crosses to WP-04 + WP-06, which can't see this WP):**
  ```csharp
  public record ChoreEquityResult(int TotalPoints, int TotalCompletions, double EqualSharePct,
      IReadOnlyList<MemberEquityShare> Members);
  public record MemberEquityShare(int UserId, string DisplayName, string Initials, string? PictureUrl,
      int Points, int Completions, double SharePct);
  ```
  (namespace `FamilyCoordinationApp.Services`). WP-04 + WP-06 preconditions restate these verbatim.
- **Window boundary (council MAJOR — pin Monday-start):** computed in `tz` local calendar. **`week` starts
  Monday**, mirroring the existing repo convention `MealPlanService.cs:179` (`daysFromMonday = (7 +
  (date.DayOfWeek - DayOfWeek.Monday)) % 7`) — P1 "extend, don't invent." This pairs with the Sunday-18:00
  default send (E-cadence) so the Sunday-evening digest reports the **full Mon–Sun week**, not an empty
  Sunday-00:00→18:00 sliver. `all` = no lower bound.
- Per member: sum `EffortPointsSnapshot` of in-window completions; also raw completion count. Members with 0
  completions appear with 0 (not omitted). **Share SCALE = PERCENT 0..100** (council MAJOR): `SharePct =
  householdTotal == 0 ? 0 : Math.Round(100.0 * memberPoints / householdTotal, 1)`; `EqualSharePct =
  memberCount == 0 ? 0 : Math.Round(100.0 / memberCount, 1)`. The island renders `{sharePct}%` **directly, no
  client multiply** (M5/M6). **No `DateTime.UtcNow` inside.** `ChoreEquityResult` holds the per-member
  distribution + totals; the endpoint (WP-06) maps it into `ChoreEquityDto` and adds the two dueness counts.
- `ChoreEquityDto`: `{ window:string, totalPoints:int, totalCompletions:int, equalSharePct:double,
  fallingBehindCount:int, upForGrabsCount:int, members: MemberShareDto[] }`; `MemberShareDto`:
  `{ userId, displayName, initials, pictureUrl?, points, completions, sharePct:double }`. **`sharePct`/
  `equalSharePct` are PERCENT 0..100** (e.g. `41.7`). Serialize with the canonical web-defaults +
  `JsonStringEnumConverter(CamelCase)` options (same as board). `window` echoes the requested window string.
  **`fallingBehindCount`/`upForGrabsCount` are computed by the WP-06 endpoint via the shared attention
  predicates (WP-05's `ChoreAttention` helper) over the household's active chores** (NOT by this calculator,
  and NOT on the board DTO — they live here so the lens + digest read one coherent equity payload). The DTO
  shape is **frozen here**.
- **Fixture must reveal the scale (council):** bake a **non-round percent** into `equity.json` (e.g. a member
  at `sharePct: 41.7`, `equalSharePct: 33.3` for a 3-member household) so the 0..100 scale is unambiguous to
  the WP-09 renderer + the contract test.
- Contract test mirrors `ChoreBoardDtoContractTests` (parsed-node compare to `equity.json`) + a
  `RenamingAField_BreaksTheFixture` non-vacuity test.
- Tests: frozen `now`+`America/Chicago` (IANA→Windows fallback like v1.0); the local-week-boundary test
  (completion at local Sun 23:59 vs Mon 00:01 lands in the correct week); effort-weighting (not counts);
  zero-completion member; empty household (no NaN).

## Verification (V1, V2)
- Unit suite green (`--filter "kind!=integration"`); `ChoreEquityDtoContractTests` matches `equity.json`;
  the board's `board.json` test still passes unchanged (E1 isolation). New files `dotnet format`-clean.

## Failure criteria
- Equity weights by raw count instead of `EffortPointsSnapshot`. · `DateTime.UtcNow` inside the calculator. ·
  Week boundary computed in UTC (wrong-day). · Divide-by-zero on empty household. · DTO not camelCase / fixture
  drift. · Any change to `board.json` or the board DTO.

## Boundary
Calculator + DTO + fixture + tests ONLY. No endpoint (WP-06), no DB query (the calculator takes already-
fetched completions+members; the endpoint/service does the query), no `Program.cs`.

## Notes for downstream
- The `ChoreEquityDto` shape is **frozen** here; WP-09's `types.ts` mirrors it + WP-06 serializes it. The
  calculator is reused by WP-04's `DigestBuilder`. The endpoint (WP-06) fetches `ChoreCompletion`s + members
  per household and passes them in.
