# Decisions — Chores v1.1 (Equity View + Weekly Discord Digest)

Decisions extend the Phase 10 v1.0 ledger (do not relitigate D1–D19 there). Numbering is fresh (E1…) to
avoid collision. Forcing tables for the genuinely contested forks; one-liners where a v1.0 constraint or an
operator call already settles it. Low-confidence / operator-vetoable items are flagged.

---

## E1: Equity read model — separate endpoint + DTO + fixture, NOT on the board DTO *(locked by M9)*

`GET /api/chores/equity?window=week` returns a new `ChoreEquityDto`, serialized with the same camelCase +
`JsonStringEnumConverter` options as the board, pinned by its **own** contract fixture
`tests/.../Fixtures/ChoreEquity/equity.json`. The frozen board DTO (`Services/Dtos/ChoreDtos.cs`,
`board.json`) is **not touched** — adding equity aggregates there would churn the M9 board contract + the
island `types.ts` in lockstep for no benefit. Reasoning: equity is a distinct concern with a distinct
cadence of change; isolating it keeps the board contract stable and the island's lens switch still does no
refetch for the four v1.0 lenses (the equity lens fetches its own payload once on first open).

## E2: Equity computation — a pure, TZ-aware calculator mirroring `ChoreStatusCalculator` *(locked idiom)*

A new pure service `ChoreEquityCalculator` (stateless, parameterless ctor; `now` + `TimeZoneInfo` as method
params) aggregates the `ChoreCompletion` log into per-member effort-weighted totals over a window. Mirrors
`ChoreStatusCalculator` exactly (server-side, unit-testable with frozen `now`+tz; no `DateTime.UtcNow`
inside). The window's local-calendar boundaries are computed in the injected `TimeZoneInfo` (M5/M6 — the
island never does date math). Effort weight = `EffortPointsSnapshot` from each completion row (the value
snapshotted at completion time, so retiering a chore later doesn't rewrite history).

## E3: Equity windows — ship `week` + `all`; default `week` *(forked; resolves OQ2)* `[DECISION: E3 — medium confidence]`

| Option | Windows | Cost | Covers the headline |
|---|---|---|---|
| A. week-only | current local week | 1 boundary calc | "who carried this week" |
| B. **week + all-time** (chosen) | + cumulative | trivial (all = no lower bound) | + "lifetime contribution" |
| C. + last-week compare | + prior week delta | +1 window + view delta UI | + "trend" |

**Chosen: B.** `window ∈ {week, all}` (validated allowlist; unknown ⇒ 400). `week` is the digest's window and
the lens default; `all` is a near-free addition (no lower bound on the completion query) that gives the view
a second tab without new math. **Last-week comparison (C) deferred** — it needs a second window + trend UI and
isn't load-bearing for "is the load shared." **Flag:** if the operator wants the trend in v1.1, add `last-week`.

## E4: Equity surface — a 5th lens in the ViewSwitcher *(forked; resolves OQ5)*

| Option | Placement | Prominence | Cost |
|---|---|---|---|
| A. **5th lens** (chosen) | peer in the view-switcher (`needs-attention\|rooms\|up-for-grabs\|mine\|equity`) | first-class | +1 `ChoreLensId`, +1 component, ViewSwitcher auto-discovers |
| B. panel in Mine | sub-section of the Mine lens | secondary | smaller, but buries it |

**Chosen: A.** The operator's north-star is "first-class, not a token strip"; a 5th lens is the honest
expression. Add `equity` to the `ChoreLens` canonical ids (`Data/Entities/ChoreEnums.cs` + island
`types.ts`/`CHORE_LENSES`, in lockstep — M6/M9) and to the `PATCH /me/default-view` allowlist (a user can
even open onto Equity). The lens fetches `/api/chores/equity` once on first activation (the only lens that
fetches; the four v1.0 lenses still group the one board payload — M11). The WP-12 held-count strip in Mine
is **superseded** by this lens (kept or removed per the WP).

## E5: Equity framing — neutral distribution + equal-share reference, no thresholds/ranking *(resolves OQ3; guardrail)*

The view shows **proportional shares** (each member's effort-points as a % of the household total) as bars,
plus a single **neutral equal-share reference** (`100% / memberCount`) — descriptive, not prescriptive. **No**
"fair/unfair" labels, **no** "behind" flags, **no** ranking sort that implies winners/losers (sort is stable
by member, e.g. current-user-first then display name). Any visual band (e.g. "near/below the reference") is a
**named tunable constant** (P4) and is purely descriptive coloring, not judgment. This honors the locked v1.0
framing ("owner is equity-surfacing, never a nag; escalate-to-visibility only"). Built dark against
synthetic data ⇒ **no fairness threshold is validated** — recorded verification gap; tune post-launch.

## E6: Digest settings — a new `HouseholdChoreDigestSettings` entity, PK `HouseholdId` *(forked)*

| Option | Shape | Multi-tenant | Forecloses multi-route? |
|---|---|---|---|
| A. column(s) on `Household` | webhook etc. on the core entity | n/a | touches an existing entity (E2/E3 v1.0 fence) |
| B. **new entity, PK `{HouseholdId}`** (chosen) | 1:1 settings row per household | clean | one row now; a future `DigestRoute` table or a `+RouteId` extends it |
| C. new entity, PK `{HouseholdId, RouteId}` | many routes per household now | clean | over-builds v1.1 (operator: single webhook now) |

**Chosen: B.** A new entity (additive, composite-key idiom; configuration auto-applied) keyed on
`HouseholdId` (1:1 with household — this is the household's *default digest* config). Fields:
`WebhookUrlProtected` (string, **ciphertext**, nullable), `Enabled` (bool, default false),
`Cadence` (enum `{Weekly}`, default Weekly — enum so Daily/Monthly aren't foreclosed), `SendDayOfWeek`
(`DayOfWeek`, default Sunday), `SendHourLocal` (int 0–23, default 18), `LastSentAt` (`DateTime?` UTC),
`CreatedAt`/`UpdatedAt` (UTC). **Not** a column on `Household` (stays within the v1.0 E2 fence — additive new
entity, not an existing-entity change). Multi-route is a future table; not foreclosed.

## E7: Webhook secret — encrypted at rest via the existing Data Protection, never logged *(locked by A2)*

Inject `IDataProtectionProvider`; create a named protector `CreateProtector("ChoreDigestWebhook")`;
store `protector.Protect(webhookUrl)` ciphertext in `WebhookUrlProtected`; `Unprotect` only at send time. The
plaintext URL is **never written to a log, an exception message, or a DTO returned to the client** (the
settings GET returns a boolean `hasWebhook` + a masked hint, never the URL). `DATAPROTECTION_CERT` is already
prod-required (`Program.cs:24-59`), so ciphertext is cert-encrypted in prod. Reasoning: a Discord webhook URL
is a capability secret (anyone holding it can post to the family channel) — CORRECTIONS "secret values leak"
discipline applies.

## E8: Digest firing — external cron → authenticated endpoint, NO `BackgroundService` *(forked; REVERSES MN1 — RATIFICATION GATE)* `[DECISION: E8 — operator-recommended, needs sign-off]`

| Option | Mechanism | Reverses MN1? | Forcing cost | Single-container fit |
|---|---|---|---|---|
| A. `BackgroundService` | in-process hosted timer sweeps due households | **yes, fully** | hosted service + scheduling + tests | textbook but reintroduces what v1.0 avoided |
| B. **external cron → endpoint** (chosen) | system/pm2 cron curls `POST /api/chores/digest/run` | **spirit preserved** (no in-process timer) | one endpoint + a cron line | ideal — app stays request-driven |
| C. lazy "digest on next visit" | compute on board open, no push | no | none | but it's not a *push* — fails the Discord-digest goal |

**Chosen: B**, and **flagged as the one explicit ratification gate** because it reverses v1.0 **MN1** ("no
`BackgroundService`/`IHostedService`"). The endpoint approach honors MN1's *spirit* — the app remains
request-driven, the schedule lives in cron — and forecloses nothing: a future `BackgroundService` could call
the same `DigestService.RunDueAsync`. Deploy wires a darktower cron (hourly) hitting the endpoint with the
token. **If the operator prefers the hosted service**, only the trigger changes; the builder/sender/service
are identical. *Sign-off recorded at the council/review gate.*

## E9: Trigger auth — shared-secret token header, fixed-time compare, outside cookie auth *(locked by A7)*

`POST /api/chores/digest/run` lives in its **own** map group **without** `.RequireAuthorization()` (cron has no
session). It reads `CHORES_DIGEST_TRIGGER_TOKEN` from configuration and requires a matching token in a request
**header** (`X-Digest-Trigger-Token`) — compared with `CryptographicOperations.FixedTimeEquals` (no timing
oracle). **If the token is unconfigured, the endpoint refuses to run** (503/`disabled`) — it never executes
unauthenticated. Query-param tokens are rejected (they leak into access logs). The endpoint is the *only*
chore route not behind cookie auth; everything else (settings, equity, edit, backfill) stays cookie-authed +
`HouseholdId`-isolated (M1).

## E10: "Due" determination — TZ day-of-week + send-hour match AND not-sent-this-window *(resolves OQ4)*

`RunDueAsync(now)` selects households where `Enabled && WebhookUrlProtected != null` and, in the app
`TimeZoneInfo`: today's `DayOfWeek == SendDayOfWeek` **and** local hour `>= SendHourLocal` **and**
`LastSentAt` is null or before the start of today's local send window. After a successful send it stamps
`LastSentAt = now`. This lets cron fire **hourly** safely: the first hourly tick on the send day at/after the
send hour sends; subsequent ticks that day see `LastSentAt` inside the window and skip (idempotent — no
double-post). Reasoning: hourly cron + a window guard is more robust than "exactly at HH:00" (a missed tick
still catches up later the same day).

## E11: Digest content — pure `DigestBuilder` → `DigestModel`; `DiscordWebhookDigestSender` renders + posts *(locked idiom)*

`DigestBuilder` (pure, testable) turns a per-household snapshot (members, this-week completions, currently
falling-behind chores, up-for-grabs count) into a `DigestModel` — reusing `ChoreEquityCalculator` (per-member
distribution) and `ChoreStatusCalculator` (falling-behind/up-for-grabs). `IDigestSender.SendAsync(webhookUrl,
DigestModel, ct)` is the boundary; `DiscordWebhookDigestSender` renders the model to a **Discord embed**
(collective headline + neutral per-member distribution + falling-behind + up-for-grabs) and POSTs via the
named `"DiscordWebhook"` `HttpClient`. **No `@mentions`, no targeted nudges** — collective broadcast only
(locked v1.0 framing). A `FakeDigestSender` records invocations for tests; **no live send in CI/local** (the
builder's output is asserted; the sender's HTTP is exercised only against a stub).

## E12: Sender resilience — named `HttpClient` + `AddStandardResilienceHandler` *(locked by A1)*

`builder.Services.AddHttpClient("DiscordWebhook").AddStandardResilienceHandler(...)` mirroring the
`RecipeScraper` setup (`Program.cs:142-157`) with Discord-appropriate options (attempt timeout ~10s, total
~30s, exponential backoff + jitter, circuit breaker). `DiscordWebhookDigestSender` resolves
`IHttpClientFactory.CreateClient("DiscordWebhook")`. Discord webhook rate limit (~30/min) is far above a
once-weekly-per-household cadence, so no custom throttle is needed; a 429 is handled by the resilience retry.

## E13: Digest orchestration — a scoped `DigestService.RunDueAsync` *(locked idiom)*

Scoped service (`IDbContextFactory`, `ChoreEquityCalculator`, `ChoreStatusCalculator`, `IDigestSender`,
`IDataProtectionProvider`, `TimeZoneInfo`, `TimeProvider`, `ILogger`). For each due household: build the
snapshot, `Unprotect` the webhook, send, stamp `LastSentAt`. **Per-household failures are caught + logged and
do not abort the run** (one bad webhook can't block other households). Returns a summary (sent/skipped/failed
counts) for the endpoint response + logs (webhook URL never in the summary).

## E14: Edit-chore dialog — island-only, drives the existing `PUT` *(no backend change)*

A `EditChoreSheet.svelte` (reusing `QuickAddSheet`'s form fields) opens from the chore card's overflow,
pre-filled from the `ChoreDto`, and calls the already-wired `updateChore(id, body)` → `PUT
/api/chores/{id}` with the card's `version` (409-aware, same optimistic/reconcile path as other mutations).
`UpdateChoreRequest` (no assignee — assignment never moves via edit, per v1.0 D6) already exists. **Zero
backend change** (the endpoint + service + DTO shipped in v1.0; only UI was missing).

## E15: Backfill — an idempotent "load starter set" endpoint, cookie-authed, household-scoped *(forked)*

`POST /api/chores/seed-starter` (cookie-authed, `HouseholdId` from `ResolveUserAsync`, M1) calls
`SeedData.SeedChoresAndRoomsAsync(dbFactory, householdId)` — already idempotent (no-ops if the household has
any rooms/chores). Any household member may run it (additive + idempotent; no roles — E1 stays clear). The
island surfaces it as a one-tap "Load starter chores" action on an empty board / in settings. Reasoning:
reuses shipped, tested seed logic; the idempotency guard makes it safe to re-tap.

## E16: Dev seed enrichment — multi-member + cross-member completions, DEV-ONLY *(locked by A5)*

`SeedData` gains a dev-only path that, after the base seed, ensures the dev household has multiple members
(realistic names — Justin/Natalie/Tristan/Samantha) and writes **backdated completions spread across them**
(direct context insert, M13, with varied `EffortPointsSnapshot`) so the Equity lens renders a real
multi-person distribution locally. Wired only into `SeedDevelopmentDataAsync` (the dev startup path), **not**
`SetupService.CreateHouseholdAsync` (a brand-new prod household legitimately has one member). Keeps prod
backfill single-user-honest while making equity locally demoable + testable.

## E17: Send-time timezone — the single app `TimeZoneInfo` (per-household TZ still deferred) *(locked, consistent with v1.0 D14)*

`SendDayOfWeek`/`SendHourLocal` and the equity window boundaries are interpreted in the app-configured
`TimeZoneInfo` (`ResolveChoresTimeZone`). Per-household TZ remains the deferred clean upgrade (v1.0 D14
flag) — schema-cheap to add later; not foreclosed. Acceptable while both families are same-region; flagged if
Trey's family is in a different timezone.

## E18: Migration — one additive `AddChoreDigestSettings` *(locked discipline)*

One additive migration (a single `CREATE TABLE ChoreDigestSettings` + index on `HouseholdId`), following the
post-incident discipline from the Phase 10 WP-08 follow-up: `dotnet build` between `migrations add` and
`migrations script`; verify the delta is additive-only; confirm the `.Designer.cs` is generated and the model
snapshot stays consistent (`has-pending-model-changes` ⇒ none). Auto-applies via `MigrateAsync` on startup.

## E19: Going live — flag flip + new env vars, documented not coded *(deploy-time)*

The bundle ships dark. Go-live = set `CHORES_USE_ISLAND=true` + `CHORES_DIGEST_TRIGGER_TOKEN=<secret>` in the
prod `.env.local`, add the hourly cron line (curl the run endpoint with the token header), and
redeploy/restart. Documented in `.env.example` + a deploy note + the morning handoff; the code does **not**
auto-enable. (Per the operator's "hold for push/PR" + "flip is the final act of the one drop.")

---

### Pragmatist check summary

- **Overruled toward more:** E4 (5th lens, not a buried panel) and E16 (dev seed enrichment) add real surface,
  but both serve the operator's "first-class, not token / must demo" bar; E6 adds an entity but keeps the
  v1.0 existing-entity fence intact.
- **Pragmatist won:** E1 (separate endpoint, board contract untouched), E8-B (cron-over-`BackgroundService`),
  E11/E12 (reuse the named-client resilience + a pure builder), E15 (reuse the shipped idempotent seed),
  E3-B (week+all, defer trend) — each takes the smaller blast radius while keeping extensions open.
- **The one deliberate reversal:** E8 reverses v1.0 MN1 (no scheduler) in the most minimal, reversible way —
  explicitly gated for operator sign-off rather than assumed.

---

## Council amendments (round 1, 2026-05-31) — decision-level refinements

The multi-model council (claude-opus / gemini / gpt) + three fresh-eyes waves surfaced refinements baked into
the WP files; recorded here so the ledger stays the source of truth. No redesign — D1–D19/E1–E19 stand.

- **CA1 → E2 (week-start):** the equity `week` window **starts Monday**, mirroring the existing
  `MealPlanService.cs:179` convention (P1 "extend, don't invent"). Pairs with the Sunday-18:00 default send so
  the digest reports a full Mon–Sun week, not an empty Sunday sliver. (opus — would otherwise be semantically
  empty every week.)
- **CA2 → E1/E2 (share scale):** `sharePct`/`equalSharePct` are **percent 0..100** (e.g. `41.7`), computed +
  rounded server-side; the island renders `{sharePct}%` directly (no client multiply). `equity.json` bakes a
  non-round value to pin the scale. (opus/gpt — fraction-vs-percent ambiguity.)
- **CA3 → E2/E11 (frozen internal contract):** `ChoreEquityResult` + `MemberEquityShare` are concrete records
  frozen in WP-02 and **restated verbatim** in WP-04/WP-06 (agents can't see sibling WPs). `DigestBuilder`
  takes a named `DigestChoreLine` record, not an anonymous tuple. (opus Critical / gpt.)
- **CA4 → E10/E13 (concurrency-safe idempotency):** the digest run **atomically claims** each due household via
  a single `ExecuteUpdateAsync(SET LastSentAt=now WHERE …window guard)` and proceeds only if it claimed the row
  — closing the read-send-stamp double-post race under concurrent cron hits. Failed sends **compensate**
  (restore `LastSentAt`) so a later tick retries. A concurrent double-fire integration test enforces it. (gpt
  Critical.)
- **CA5 → E5/E13 (shared attention predicate):** `fallingBehindCount`/`upForGrabsCount` are computed via ONE
  shared `ChoreAttention` helper used by both the equity endpoint (WP-06) and the digest (WP-05), so the lens
  and the digest never disagree. (opus/gpt.)
- **CA6 → E6/E7 (settings contract):** digest-settings enums serialize **camelCase** (`cadence:"weekly"`,
  `sendDayOfWeek:"sunday"…`); the `webhookUrl` update is a frozen **tri-state** (omit=unchanged / non-blank=
  replace / null|""=clear), identical across WP-03/06/11; a small casing contract assertion guards drift. (gpt.)
- **CA7 → E14 (edit-photo):** the frontend `UpdateChoreRequest` gains `photoPath?`; the edit dialog wires
  upload-then-PUT (the backend `PhotoPath` already existed; the TS type omitted it). The edit affordance threads
  an `onEdit` prop through all four lens boards that render `ChoreCard`. (gpt Critical.)
- **CA8 → E1 (equity cache):** the island invalidates the cached equity payload on `complete`/`seedStarter`/
  board-refetch so the lens never shows a stale distribution. (gpt.)
- **CA9 → E6 (entity):** `HouseholdChoreDigestSettings.HouseholdId` PK is `ValueGeneratedNever()` (caller-
  supplied, not identity); no separate index (PK is the index). (gpt.)
- **CA10 → WP-05 (DST):** `sendWindowStartUtc` guards `tz.IsInvalidTime` (spring-forward gap) before
  `ConvertTimeToUtc`, matching `ChoreStatusCalculator.LocalMidnightUtc`'s idiom. (fresh-eyes wave 2.)

Gemini rated the spec ready; opus and gpt each found real blockers the other missed (contract-freeze vs
concurrency-idempotency) — no single lens caught all. Net: amendments, not redesign.
