# Constraints — Chores v1.1 (Equity View + Weekly Discord Digest)

Extends the Phase 10 v1.0 constraint set (M1–M12 / MN1–MN8 / P1–P5 / E1–E6 there). Restated/renumbered here
for self-containment; **v1.1-specific** items are marked ⊕. Cross-referenced against
`D:\Development\data\memory\CORRECTIONS.md` (secrets-leak, date-parsing, Svelte-runes, deferred-MCP, spec-depth).

## Musts

1. **M1 — HouseholdId isolation.** Every new cookie-authed endpoint (equity, digest-settings, backfill) resolves
   `HouseholdId` via `UserContextResolver.ResolveUserAsync` and filters by it; never client-supplied. ⊕ The
   digest-run endpoint iterates households **server-side** (no client household input).
2. **M2 — DbContextFactory.** New services take `IDbContextFactory<ApplicationDbContext>`, one short-lived
   context per op; never inject `DbContext`.
3. **M3 — Composite-key idiom.** `HouseholdChoreDigestSettings` follows the entity idiom (PK keyed on
   `HouseholdId`, `IEntityTypeConfiguration` auto-applied via `ApplyConfigurationsFromAssembly`). ⊕
4. **M4 — Gates clean before commit.** `dotnet build` + `dotnet format --verify-no-changes` (changed files) +
   `npm run build` + `npx svelte-check` per the relevant WP.
5. **M5/M6 — UTC + injected-TZ date math.** Equity windows + digest send-time computed server-side from UTC +
   injected `TimeZoneInfo`; the island renders server-computed values and does **no** client date math.
6. **M7 — Equity DTO/fixture lockstep.** ⊕ Any `ChoreEquityDto` shape change updates the island `types.ts`
   **and** `equity.json` together; the board DTO (`board.json`) stays untouched (E1 isolation).
7. **M8 — Webhook encrypted at rest.** ⊕ The Discord webhook URL is stored as Data-Protection ciphertext
   (`CreateProtector("ChoreDigestWebhook")`); decrypted only at send time.
8. **M9 — Trigger-token discipline.** ⊕ `POST /api/chores/digest/run` validates `CHORES_DIGEST_TRIGGER_TOKEN`
   from a request **header** with `CryptographicOperations.FixedTimeEquals`; **refuses to run** (503) if the
   token is unconfigured.
9. **M10 — Digest idempotency + failure isolation.** ⊕ A run sends at most once per household per cadence
   window (`LastSentAt` guard); a per-household send failure is caught + logged and does not abort the run.
10. **M11 — Collective broadcast only.** ⊕ The digest is an ambient channel post — **no `@mentions`, no
    targeted nudges**; `allowed_mentions` suppresses pings (defense-in-depth). (Locked v1.0 framing:
    escalate-to-visibility, owner is equity-surfacing never a nag.)
11. **M12 — Equity descriptive, not prescriptive.** ⊕ Neutral proportional shares + a single equal-share
    reference; no ranking that implies winners/losers, no "behind"/fair/unfair labels; any band is a tunable
    constant (P4).
12. **M13 — Additive migration.** ⊕ One `CREATE TABLE` (+ index); `.Designer.cs` generated; model snapshot
    consistent (`has-pending-model-changes` ⇒ none); auto-applies via `MigrateAsync`.
13. **M14 — Reuse the resilience idiom.** ⊕ The sender uses a named `"DiscordWebhook"` `HttpClient` +
    `AddStandardResilienceHandler` (mirroring `RecipeScraper`), not a hand-rolled Polly policy.
14. **M15 — Timestamps + enum serialization.** Timestamps are `DateTime` Kind=Utc; DTO enums serialize as
    camelCase strings via the global `JsonStringEnumConverter` (the equity endpoint uses the same options).
15. **M16 — Canonical `equity` lens id.** ⊕ `equity` is added to the `ChoreLens` ids (`Data/Entities/
    ChoreEnums.cs`), the island `CHORE_LENSES`/`ChoreLensId` (`types.ts`), and the `/me/default-view`
    allowlist — defined once, no ad-hoc casings (council M6 lineage).

## Must-Nots

1. **MN1 — No `BackgroundService`/`IHostedService`.** ⊕ Even though v1.1 adds scheduled digests, the firing
   stays **external-cron → endpoint** (E8 ratified). Do not add an in-process hosted timer. *(If cron proves
   infeasible mid-build, STOP — escalation E3, do not silently add a hosted service.)*
2. **MN2 — No `DataNotifier`/`PresenceService`/`PollingService`** for island liveness (circuit-bound).
3. **MN3 — No other v1.1+ layers.** Do not build roles/permissions, parent-approval, kid UI, subtasks, chore
   dependencies, SSE, cross-household sharing, web push, or email. The **only** new surface is the equity
   view, the Discord digest, and the two table stakes (edit-chore, backfill).
4. **MN4 — Do not perturb the frozen board DTO / `board.json`.** ⊕ Equity is a separate endpoint + fixture.
5. **MN5 — No new columns on existing entities** (`Household`/`User`/`Chore`/…). ⊕ Digest config is its own
   entity (keeps the v1.0 existing-entity fence; the only ever-permitted such column was `User.ChoresDefaultView`).
6. **MN6 — Do not touch the shopping-list island/endpoints or its Docker stage** — add parallel lines/targets.
7. **MN7 — Never log, return, or surface the webhook plaintext.** ⊕ Not in logs, exception messages, the
   settings GET, or the run summary. (CORRECTIONS: secret values leak to conversation/logs.)
8. **MN8 — No targeted nudges.** ⊕ (Reinforces M11.) No per-person ping, DM, or "you owe N chores" callout.
9. **MN9 — No client-side date math.** No `new Date('YYYY-MM-DD')` in island TS (CORRECTIONS date footgun);
   render server-computed equity/dueness only.
10. **MN10 — The run endpoint must not be reachable unauthenticated**, and must not accept the token via query
    string (leaks to logs). ⊕
11. **MN11 — Do not enrich the production household with synthetic multi-member completions.** ⊕ The
    cross-member seed enrichment is **dev-only** (`SeedDevelopmentDataAsync`); `CreateHouseholdAsync` stays
    single-user-honest. Production backfill (E15) creates only the standard starter rooms/chores.
12. **MN12 — Do not auto-enable `CHORES_USE_ISLAND` in code.** Going live is a deploy-time flag flip (E19).
13. **MN13 — Svelte runes:** do not export a reassigned `$state`/`$derived`; export the store instance / use
    accessors (CORRECTIONS / v1.0 island convention).

## Preferences

1. **P1 — Extend, don't invent.** Reuse `ResolveUserAsync`, DTO records, the `ChoreStatusCalculator` pure-
   calculator idiom, the named-`HttpClient` resilience pattern, `SeedData`, and the island store/`api.ts`/lens
   conventions.
2. **P2 — Compute on read; append-only logs.** Equity is computed from `ChoreCompletion` on read; the only new
   stored state is the settings row (`LastSentAt` is its single denormalization).
3. **P3 — Effort as named tiers in UI; weight by `EffortPointsSnapshot`** in equity (never a raw editable number).
4. **P4 — Named constants for tunables:** cadence default (Sunday 18:00 local), equity descriptive band(s),
   `"DiscordWebhook"` resilience timeouts, the cron tick assumption.
5. **P5 — Keep `Chores.razor` a thin mount + flag check.**
6. **P6 — Pure builder/calculator; thin sender; faked in tests.** No live network in the unit/integration suite.

## Escalation Triggers

1. **E1 (roles):** if equity or the digest appears to require the deferred `MemberRole` scaffold, **stop and
   flag** — do not pull roles forward (it should not; both work over existing `User` rows).
2. **E2 (schema/infra):** any change to an existing entity beyond the new `HouseholdChoreDigestSettings` table,
   or any `Program.cs`/`ApplicationDbContext` change beyond DbSet+config / service+endpoint+`HttpClient`+
   config registration → escalate.
3. **E3 (firing reversal):** E8 reverses MN1 in the most minimal way (cron→endpoint). If during build a
   `BackgroundService` seems necessary (cron infeasible, or a requirement emerges), **STOP and get operator
   sign-off** — do not silently add an in-process scheduler. *(This is the ratification gate.)*
4. **E4 (CI Docker):** if Testcontainers can't run where the suite runs, escalate — do **not** downgrade the
   digest-run / isolation tests to mocks.
5. **E5 (recurrence):** unchanged from v1.0 — no monthly-on-day; reject at creation, don't expand the engine.
6. **E6 (existing security):** if a security issue is found in existing endpoints/auth while mirroring the
   pattern, document + escalate — do not fix it inside these WPs.
7. **E7 (secret handling):** ⊕ if the webhook can't be encrypted cleanly via the existing Data Protection,
   **STOP** — do not fall back to storing it in plaintext.
8. **E8 (HOLD push/PR):** ⊕ operational — build, test, and **local** commits on `feat/chores-v1.1-equity-digest`
   only. **Do not push and do not open a PR** until the operator reviews. (And: do not flip the prod flag.)
