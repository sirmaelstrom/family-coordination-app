# Review Lens: claude-opus
**Workshop:** Chores v1.1 — Household Equity View + Weekly Discord Digest
**Surface:** spec
**Round:** 1
**Duration:** 352648ms
**Status:** success

---

I have enough verification. The spec's codebase claims check out (file paths, line numbers, `ChoreLens.All`, `MemberDto`, `RecipeScraper` resilience block, Data Protection, `ResolveChoresTimeZone`, the `LENS_LABEL: Record<ChoreLensId,string>` exhaustive map, `updateChore`). One notable discovery: `MealPlanService.cs:179` already establishes a **Monday-start week** convention that the equity spec never references. Now the review.

---

# Spec Review — Chores v1.1 (Equity View + Weekly Discord Digest)
## Reviewer: claude-opus · Lens: Reasoning & Logic

The artifacts are unusually disciplined — the assumption ledger, the E2/E3-fence reasoning, and the explicit MN1-reversal ratification gate are all sound. The codebase claims I spot-checked are accurate. My findings cluster around **internal contract types that cross WP boundaries but are never frozen**, one **genuine semantic contradiction** (week-start vs. send-day), and a **scale ambiguity** that will silently render 100× wrong. These are logic/contract problems, which is my lane.

---

### Per-Package Findings

**WP-02: Equity calculator + DTO + fixture**

- **Finding (Critical): `ChoreEquityResult` is a cross-WP contract type but its shape is never specified.** The *DTO* (`ChoreEquityDto`/`MemberShareDto`) is frozen and pinned by `equity.json`. But `ChoreEquityResult` — the type `Compute(...)` actually returns — is the boundary consumed by **WP-04** (`DigestBuilder.Build(householdName, ChoreEquityResult equity, …)`) and **WP-06** (maps it to the DTO). Agents see only the orchestrator + their own WP. WP-04's agent has never seen WP-02 and must read per-member `DisplayName/Points/SharePct` off `ChoreEquityResult`; WP-06's agent must read totals + per-member off it. Neither WP states the field names or the per-member item type. This is a compile break waiting at wave 3 and wave 5.
  - **Location:** WP-02 "Implementation notes" ("`ChoreEquityResult` holds the per-member distribution + totals") and "Notes for downstream" (freezes only the DTO).
  - **Recommendation:** Freeze `ChoreEquityResult` concretely in WP-02 *and* restate it verbatim in WP-04's and WP-06's preconditions. E.g. `record ChoreEquityResult(int TotalPoints, int TotalCompletions, double EqualShare, IReadOnlyList<MemberEquityShare> Members)` and `record MemberEquityShare(int UserId, string DisplayName, string Initials, string? PictureUrl, int Points, int Completions, double Share)`. Name the per-member type so WP-04/06 can reference it without guessing.

- **Finding (Major): "Start of current local week" is undefined, and the default collides with it.** E2/WP-02 say `week = start of current local week … now` but never name the first day. US locale defaults to Sunday; the codebase already has a **Monday-start** convention (`MealPlanService.cs:179-180`: `daysFromMonday = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7`). The default send is **Sunday 18:00** (P4/OQ1). If an agent picks Sunday-start, the Sunday-evening digest window is *Sunday 00:00 → 18:00* — it reports only that afternoon's completions and the headline "knocked out N chores this week" is near-empty every week. This is a real semantic bug, not a style nit.
  - **Location:** decisions.md E2; WP-02 implementation notes; P4 cadence default; collides with the WP-04 headline.
  - **Recommendation:** Pin "week starts Monday, mirroring `MealPlanService` week-start" in WP-02 (honors P1 "extend, don't invent"). Monday-start + Sunday-18:00 send captures the full Mon–Sun week — confirm that's the intent and state it.

- **Finding (Major): `sharePct`/`equalSharePct` — fraction (0..1) or percent (0..100)?** WP-02 documents the values as `memberPoints / householdTotal` and `1.0 / memberCount` (both **fractions**, e.g. `0.25`), but names the fields `sharePct`/`equalSharePct` and WP-09 renders "share %". The fixture pins a number but not its scale to the renderer's mental model. If WP-09's agent does `{member.sharePct}%`, it shows `0.25%`; if it does `{sharePct * 100}%` against a backend that already multiplied, it shows `2500%`. The fixture (a bare `number`) does not disambiguate intent across the boundary.
  - **Location:** WP-02 DTO definition vs. implementation formula; WP-09 EquityBoard render.
  - **Recommendation:** Either rename to `shareFraction`/`equalShareFraction` and document 0..1 (WP-09 multiplies by 100 for display), or compute `* 100` server-side and document 0..100. State the chosen scale in both WP-02 and WP-09, and bake a non-round value into `equity.json` (e.g. one member at `0.6` / `60`) so the fixture itself reveals the scale.

**WP-05: DigestService.RunDueAsync**

- **Finding (Major): "up-for-grabs" / "falling-behind" are computed independently here and in WP-06 — they can diverge.** WP-06 gives a precise predicate for `upForGrabsCount` (`assignmentKind == none || isClaimStale`) and `fallingBehindCount` (`Overdue || DueToday`). WP-05 only says "compute via `ChoreStatusCalculator.Compute` over the household's chores" — loosely. The lens and the digest are supposed to tell the same story; if WP-05's agent counts up-for-grabs as "assignmentKind == none" only (omitting stale claims), the Sunday digest and the Monday lens disagree for the same household. There is no shared helper (the `ChoreBoardService` equivalents at `ChoreBoardService.cs:192-195` are `private static`).
  - **Location:** WP-05 implementation notes ("compute via `ChoreStatusCalculator.Compute`") vs. WP-06's explicit predicate.
  - **Recommendation:** Define both predicates once and reference the same definition from WP-05 and WP-06 (extract an `internal static` helper, or restate the exact predicate text in WP-05). Cross-link them.

- **Finding (Minor): Dependency list omits WP-02.** WP-05's ctor injects `ChoreEquityCalculator equity` (WP-02) and the precondition names it, but the **Depends on** header lists only WP-01/03/04 and the orchestrator mermaid has no `WP02 → WP05` edge. Wave order (WP-02 wave 2 < WP-05 wave 4) saves it, but the declared dependency graph is wrong.
  - **Recommendation:** Add WP-02 to WP-05's Depends-on and the mermaid edge.

- **Finding (Minor): Double-read of the settings row.** RunDueAsync queries `ChoreDigestSettings` to find due households, then calls `settings.GetDecryptedWebhookAsync(householdId)` which re-queries the same row by id. Harmless but wasteful per household.
  - **Recommendation:** Either accept it (note it) or have `GetDecryptedWebhookAsync` take the already-loaded ciphertext. Low priority.

**WP-06: Endpoints + Program.cs wiring**

- **Finding (Minor): Dependency declaration omits WP-04.** WP-06 registers `IDigestSender → DiscordWebhookDigestSender` and `AddSingleton<DigestBuilder>()` — both WP-04 types — and the named `"DiscordWebhook"` client that WP-04's sender resolves. The precondition mentions "builder/sender (WP-04) exist," but **Depends on** lists only WP-02/03/05 and the mermaid has no `WP04 → WP06` edge. Ordering is safe (wave 3 < 5); the graph is incomplete.
  - **Recommendation:** Add WP-04 to the Depends-on and mermaid.

- **Finding (Minor, positive — verify only): resilience options are valid.** I checked the `AddStandardResilienceHandler` constraints against the real `RecipeScraper` block (`Program.cs:142-157`): `SamplingDuration (60s) ≥ 2 × AttemptTimeout (10s)` ✓ and `AttemptTimeout ≤ TotalRequestTimeout (30s)` ✓. No change needed — flagging only because these throw at startup if violated and the WP hardcodes them.

**WP-01: Digest-settings entity + migration**

- **Finding (Minor): W1 gate generates the full script, not the delta.** Gate W1 runs `dotnet ef migrations script --no-build`, which (no `from`/`to` args) emits the **entire** schema script. WP-01's verification says "delta = a single `CREATE TABLE`" — an agent running the gate gets the whole-chain script and must eyeball it. The intended check is `migrations script <previousMigration> <newMigration>` (or `migrations has-pending-model-changes`, which WP-01 does list).
  - **Recommendation:** Have the gate emit the bounded delta, or instruct the agent to diff against the prior migration; lean on `has-pending-model-changes ⇒ none` as the additive check.

- **Finding (Minor): "+ index on HouseholdId" wording conflicts with PK.** E18/M13 say "one `CREATE TABLE` (+ index on `HouseholdId`)" but WP-01 correctly notes the PK *is* `HouseholdId`, so no separate index exists. The orchestrator/constraint phrasing implies a redundant second index.
  - **Recommendation:** Reword E18/M13 to "PK on `HouseholdId` (serves as the index)"; otherwise an agent may add a duplicate index and the migration delta won't match the "single CREATE TABLE" expectation.

**WP-08: Integration tests**

- **Finding (Minor): `ConfigureTestServices` vs. the existing factory's `ConfigureServices`; and the seed uses wall-clock `now`.** The harness overrides services in `ConfigureWebHost → builder.ConfigureServices` (`ChoresWebAppFactory.cs:80-92`), and `SeedAsync` stamps `now = DateTime.UtcNow` (`~line 134`). WP-08 wants a **fixed** `TimeProvider` via `ConfigureTestServices` *and* households "due at that fixed now." The fixed instant and the seed's backdated rows must be computed from the *same* fixed clock, not the wall clock — otherwise "due now" is itself flaky. WP-08 acknowledges this but should pin the mechanism.
  - **Recommendation:** Specify: register `FixedTimeProvider` (already exists — `ChoreServiceConcurrencyTests.cs:84`) replacing `TimeProvider.System`, and compute the seeded `SendDayOfWeek`/`SendHourLocal` and completion timestamps from that same fixed instant inside the guarded `SeedAsync`. Note that the override must run *after* the app's `AddSingleton(TimeProvider.System)` (use `ConfigureTestServices`, which runs last).

**WP-09: Island Equity lens**

- **Finding (Minor): Disposition of the WP-12 held-count strip is unassigned.** problem-statement and E4 say the Mine-lens held-count strip is "superseded by this lens (kept or removed per the WP)," but WP-09's Files list doesn't include `MineView.svelte` and no WP owns the removal decision. It will simply be left as dead/duplicate equity surface.
  - **Recommendation:** Either add `MineView.svelte` to WP-09's scope with an explicit "remove the held-count strip" instruction, or state in WP-09 that it stays untouched. Don't leave "kept or removed per the WP" when no WP decides.

- **Finding (Minor): island gate doesn't run the dotnet bundle copy it claims to verify.** WP-09 verification says "`dotnet build` copies the bundle," but the W7–W9 gate is `npm ci && npm run build && npx svelte-check` only.
  - **Recommendation:** Add `dotnet build` to the island gate if the bundle-copy integration is part of the pass/fail, or drop that claim from verification.

---

### Cross-Cutting Findings

1. **Internal contract types crossing WP boundaries are under-frozen.** The spec rigorously freezes *DTOs* (with fixtures) but treats *internal* hand-off types (`ChoreEquityResult` + its member item; the `IReadOnlyList<(string name, DueState)> choreDueness` tuple shape between WP-05 and WP-04) as if agents share a namespace view — they don't. Every type that flows from a WP an agent can't see must be written into the consuming WP. This is the single highest-risk pattern in the bundle.

2. **The same derived quantity is computed in two places with no shared definition.** `fallingBehindCount`/`upForGrabsCount` live in WP-06 (lens path) and WP-05 (digest path). Same concept, two agents, two waves, no shared helper. Whenever a metric is rendered in two surfaces from two code paths, the spec must give one canonical predicate. (See WP-05 finding.)

3. **Week-start convention exists in-repo but is invisible to the spec.** `MealPlanService` already starts weeks on Monday. P1 says "extend, don't invent," yet WP-02 invents an unspecified week boundary. Pointing the equity calculator at the existing convention closes both the ambiguity *and* the Sunday-send collision.

---

### Targeted Analysis Results

**A. Self-Containment Audit — WP-04 (wave 3) reading as if WP-02 (wave 2) were unseen.** WP-04's `DigestBuilder.Build(householdName, ChoreEquityResult equity, IReadOnlyList<(string,DueState)> choreDueness, int upForGrabsCount)` requires me to know `ChoreEquityResult`'s members and field names to project `DigestMemberLine { DisplayName, Points, SharePct }`. WP-04 never states them; nothing in WP-04 or the orchestrator defines `ChoreEquityResult`. **I could not implement WP-04 without guessing.** This confirms Cross-Cutting #1 as the top defect.

**B. Interface Contract Check — tracing `sharePct` end to end.** `ChoreEquityCalculator.Compute` (WP-02, value = `memberPoints/householdTotal`, a fraction) → `ChoreEquityResult.Members[].Share` (un-named, un-frozen) → WP-06 maps to `MemberShareDto.sharePct` (DTO says `double`, "Pct") → `equity.json` (bare number) → WP-09 `MemberShareDto.sharePct` mirror → `EquityBoard.svelte` renders "share %". **Two breaks on one field:** (a) the intermediate `.Share` member name is unspecified (A), and (b) the fraction-vs-percent scale flips between the formula and the `Pct` name (WP-02 finding). The field name is *consistent* across packages; its *meaning* is not.

**C. Blast-Radius Scan — Files completeness.** Mostly complete. Two gaps: (1) WP-09 excludes `MineView.svelte` though E4 says the WP-12 strip is "superseded" — the Boundary ("equity lens island files ONLY") actively excludes the file that needs the strip removed. (2) WP-08 lists `ChoresWebAppFactory.cs` as "modify if needed" but the TimeProvider override + fixed-clock seeding is *required*, not optional, given the determinism mandate — under-scoped wording. No incorrect "unchanged" claims found in backend WPs; the `Program.cs` consolidation into WP-06 is clean and the E2 fence holds.

**D. Wave-Ordering Challenge.** The order is sound and correctly front-loads the digest seam's risk behind WP-08. One free optimization: **WP-07 (dev seed) has zero dependencies and could move to Wave 1** alongside WP-01 (it's currently a dangling node in the mermaid with no edges), shortening the critical path to the island WPs by one wave with no risk. Not required. The forced-sequential island chain (09→10→11 on shared files) is correct. No risk-reducing reorder is *necessary*; the plan does not front-load risk badly.

---

### Overall Assessment

**Verdict: Needs amendments.**

The architecture is well-reasoned and the codebase claims are accurate — the MN1-reversal gate (E8), the secret-handling discipline (E7/M8/MN7), the DST-safe `IsInvalidTime` guard in WP-05, and the `UseStatusCodePagesWithReExecute` empty-body footnote in WP-06 all show real engineering foresight, and the state machine for "due → send → stamp → skip" (E10) is correct and idempotent. **What must change before dispatch is small in volume but high in impact:** (1) freeze `ChoreEquityResult` and its member item type and restate it in WP-04/WP-06 — without this, WP-04 cannot compile (Critical); (2) pin the week-start day (Monday, per `MealPlanService`) so the Sunday-evening digest isn't semantically empty (Major); (3) resolve the `sharePct` fraction-vs-percent scale and bake a revealing value into `equity.json` (Major); (4) give `fallingBehindCount`/`upForGrabsCount` one canonical predicate shared by WP-05 and WP-06 (Major). The dependency-graph omissions (WP-02→05, WP-04→06) are cosmetic given wave order but should be corrected for honesty. Fix the four substantive items and this is ready.
