# Verification — Chores v1.1 (Equity View + Weekly Discord Digest)

Each criterion names its **layers** (Unit / Fixture-contract / Seam-integration / Deployment / Manual) per the
verification pattern. Integration = the WP-08 Testcontainers PostgreSQL + `WebApplicationFactory` +
`TestAuthHandler` harness (`tests/.../Integration/`), `[Trait("kind","integration")]`. Unit suite stays
runnable without Docker (`--filter "kind!=integration"`).

---

## V1 — Equity calculator (E2) is correct and timezone-aware
**Layers:** Unit.
- `ChoreEquityCalculator` unit tests with frozen `now` + injected `TimeZoneInfo`, no `DateTime.UtcNow` inside.
- Cases: per-member effort-weighting sums `EffortPointsSnapshot` (NOT raw counts); `week` window includes only
  completions inside the local-calendar week and excludes the prior week (a completion at local Sun 23:59 vs
  Mon 00:01 lands in the right week — the local-boundary test, mirroring v1.0's local-midnight test);
  `all` window has no lower bound; a member with zero completions appears with 0 share (not omitted); empty
  household ⇒ empty distribution, no divide-by-zero on the equal-share reference.
- **Observer test:** Given a fixture of completions across 3 members with known tiers, `Compute(window=week)`
  returns the exact per-member point totals and shares an independent reader can recompute by hand.

## V2 — Equity DTO contract is frozen and camelCase (E1)
**Layers:** Fixture-contract.
- `ChoreEquityDtoContractTests` serializes a known `ChoreEquityDto` with the canonical JSON options (web
  defaults + `JsonStringEnumConverter(CamelCase)`) and asserts it matches `Fixtures/ChoreEquity/equity.json`
  (parsed-node compare, escape/whitespace-tolerant). A `RenamingAField_BreaksTheFixture` test proves the
  tripwire is non-vacuous.
- **Observer test:** changing the DTO shape without updating `equity.json` **and** the island `types.ts`
  fails this test (M9 tripwire, equity edition). The board's `board.json` test still passes unchanged (proves
  E1 isolation — board contract untouched).

## V3 — Equity endpoint returns isolated, validated data (E1/E3)
**Layers:** Seam-integration.
- `GET /api/chores/equity?window=week` → 200 `ChoreEquityDto` for the caller's household only; `window=all`
  → 200; `window=bogus` → 400; unauthenticated → rejected (4xx, per the app's `UseStatusCodePagesWithReExecute`
  empty-404→400 quirk — assert "rejected, never 200/leak", not a strict code).
- Cross-household isolation: a caller in Household A never sees Household B's completions in the distribution.
- **Observer test:** seed two households with distinct completion sets; each caller's equity payload sums only
  its own household's points.

## V4 — Digest settings entity persists, is encrypted, and never leaks the URL (E6/E7)
**Layers:** Unit + Seam-integration.
- Unit: round-trip `Protect`→store→`Unprotect` returns the original URL; the stored `WebhookUrlProtected`
  value is **not** equal to the plaintext (it's ciphertext).
- Integration: `PUT/POST /api/chores/digest-settings` persists per-household; `GET` returns `hasWebhook:true`
  + masked hint, **never the URL**; the URL never appears in any log line, exception message, or run-summary
  (assert via a log sink / by inspecting the response + summary objects).
- **Observer test:** save a webhook, read the settings back over HTTP, grep the response + captured logs —
  the plaintext URL string appears nowhere; only `Unprotect` at send time reveals it.

## V5 — Digest builder produces a correct, non-punitive model (E11)
**Layers:** Unit.
- `DigestBuilder` over a fixture household yields a `DigestModel` with: correct collective totals
  (chores completed + effort-points this week), the per-member neutral distribution (= E2's output),
  the falling-behind list (from `ChoreStatusCalculator` Overdue/DueToday), and the up-for-grabs count.
- **Non-punitive assertions:** the model contains **no** `@mention`/userId-targeting field intended for a
  nudge, **no** "behind"/ranking flag; member entries are a neutral distribution. (A test asserts the model
  has no field that would render a targeted callout.)
- **Observer test:** the rendered text/model can be read aloud as "the house did X; here's the spread; these
  are falling behind; N up for grabs" with no sentence aimed at shaming a person.

## V6 — Discord sender renders + posts without live sends in CI (E11/E12)
**Layers:** Unit + Fixture-contract.
- Unit: `DiscordWebhookDigestSender` renders a `DigestModel` to the Discord webhook JSON payload (embed) shape;
  assert the payload structure (title/fields) against an expected fixture; assert **no `content` with
  `@mentions`** and `allowed_mentions` set to suppress pings (defense-in-depth).
- The HTTP POST is exercised only against a **stub** `HttpMessageHandler` (asserts the request body + that a
  non-2xx is surfaced via the resilience handler), **never a real Discord endpoint**.
- **Observer test:** the test suite makes zero outbound network calls to discord.com (verifiable by the stub
  handler being the only handler wired in tests).

## V7 — Digest-run endpoint: token auth, idempotency, isolation (E8/E9/E10/E13)
**Layers:** Seam-integration (the centerpiece, real Postgres).
- **Token auth:** `POST /api/chores/digest/run` with a valid `X-Digest-Trigger-Token` → 200; missing/wrong
  token → 401/403; **token unconfigured in app config → 503/disabled** (never runs unauthenticated); a token
  in the query string is ignored/rejected.
- **Idempotency:** two runs within the same cadence window send **at most once** per household (second run sees
  `LastSentAt` in-window and skips) — assert via the `FakeDigestSender` invocation count (exactly 1 per due
  household across two back-to-back runs).
- **Due logic:** a household whose `SendDayOfWeek`/`SendHourLocal` (in app TZ) matches `now` and is enabled +
  configured is sent; one not due / disabled / unconfigured is skipped.
- **Multi-tenant isolation:** with two due households each with its own (fake) webhook, each receives exactly
  its own `DigestModel` (built from its own data); no cross-household content bleed.
- **Failure isolation:** a sender that throws for Household A does not prevent Household B's send; the run
  returns sent/skipped/failed counts.
- **Observer test:** drive the endpoint twice against a seeded two-household container; the fake sender log
  shows one send per due household, correct payloads, no leak, and the failure case is isolated.

## V8 — Edit-chore dialog drives the existing PUT (E14)
**Layers:** Deployment (island build) + Manual.
- Island: `npm run build` + `npx svelte-check` green with the new `EditChoreSheet`; the dialog calls
  `updateChore(id, body)` with the card `version` and follows the 409-aware reconcile path.
- **Manual (owed):** with `CHORES_USE_ISLAND=true` against a seeded household, open a chore, edit name/room/
  recurrence/effort, save → board reflects the change; a stale version → "someone changed it" reconcile.
- **Observer test (automatable part):** a unit/integration test confirms `PUT /api/chores/{id}` still applies
  the update server-side (this endpoint shipped in v1.0; add a regression test if not already covered).

## V9 — Backfill is idempotent and household-scoped (E15)
**Layers:** Seam-integration.
- `POST /api/chores/seed-starter` on an **empty** household creates the starter rooms/chores; a **second**
  call no-ops (idempotency guard); a household that already has chores is untouched; the seed is scoped to the
  caller's `HouseholdId` (never another household's).
- **Observer test:** call twice; row counts after call 1 == after call 2; a second household is unaffected.

## V10 — Dev seed renders a real multi-member equity distribution (E16)
**Layers:** Unit.
- A unit test over the dev-seed output asserts ≥3 members exist and completions are authored by **more than
  one** `CompletedByUserId`, with varied `EffortPointsSnapshot` — so the equity distribution is non-trivial.
- **Observer test:** run the dev seed, query completions grouped by `CompletedByUserId` → more than one
  member has non-zero points.

## V11 — Migration is additive and auto-applies (E18)
**Layers:** Deployment.
- `dotnet ef migrations script` shows the delta is a single `CREATE TABLE ChoreDigestSettings` (+ index), no
  DROP/ALTER on existing tables; `.Designer.cs` present; `has-pending-model-changes` ⇒ none.
- The integration harness boots the real `Program.cs` via `MigrateAsync` against a fresh container (the
  fresh-DB proof) — green means the new migration applies cleanly on top of the v1.0 chain.

## V12 — Outbound resilience mirrors the established pattern (E12)
**Layers:** Unit/Deployment.
- `dotnet build` green with the named `"DiscordWebhook"` `HttpClient` + `AddStandardResilienceHandler`
  registered in `Program.cs`; a unit test asserts a 429/5xx from the stub handler is retried/surfaced per the
  resilience config (not swallowed silently).

## V13 — Flag-gating and go-live (E19)
**Layers:** Deployment.
- With `CHORES_USE_ISLAND` unset, `/chores` renders the "not enabled" placeholder (unchanged v1.0 behavior);
  set true ⇒ the island (now including the Equity lens + edit dialog) mounts. `.env.example` documents
  `CHORES_DIGEST_TRIGGER_TOKEN` + the cron line; the app does not auto-enable.
- **Observer test:** toggling the flag flips the surface; no code path enables the island without it.

## V14 — Build/format/island gates green per WP
**Layers:** Deployment.
- Every backend WP: `dotnet build` + `dotnet test --filter "kind!=integration"` green. After the integration
  WP: full suite green **with** Docker; unit suite still green **without**. Every island WP: `npm run build` +
  `npx svelte-check` (NOT `tsc` alone). New files `dotnet format --verify-no-changes` clean (scoped to changed
  files — pre-existing Program.cs/Service format debt from v1.0 is out of scope).

---

## Verification Gaps (uncovered seams + why acceptable)

- **GAP-1 — Live Discord delivery is NOT automatically verified.** No real webhook is available to the build,
  and the family channel must not be spammed. The builder's model + the sender's payload shape are unit-tested
  against a stub; the end-to-end "a real message appears in Discord" is a **2-minute manual check** for the
  operator (paste a test webhook into settings → hit the run endpoint with the token). *Acceptable:* the only
  unverified link is the literal discord.com POST, whose request shape is asserted; the operator closes it on
  first real config. Recorded for the morning handoff.
- **GAP-2 — Island UI click-through is manual.** Google OAuth + the absence of a browser test harness (carried
  from v1.0) mean the Equity lens, edit dialog, and settings surface are build-/svelte-check-verified, not
  in-browser-verified by automation. *Acceptable + flagged:* the data layers (equity endpoint, settings,
  digest) are integration-tested on real Postgres; the UI is the manual-owed surface, explicitly not claimed
  as UI-verified.
- **GAP-3 — Equity fairness thresholds are unvalidated.** Built dark against synthetic/seed data; no real
  household usage exists to calibrate any descriptive band. *Mitigation (E5):* ship descriptive-only (neutral
  shares + equal-share reference), all bands as tunable constants (P4), tuned post-launch. This is the
  operator-accepted consequence of bundling dark.
- **GAP-4 — Real cron scheduling on the darktower is deploy-time.** The endpoint + due-logic are
  integration-tested by *invoking* the endpoint; the actual cron entry firing on schedule is verified manually
  post-deploy (and is trivially observable via `LastSentAt` + a Discord post). *Acceptable:* cron correctness
  is an ops concern, not app logic; the app logic (idempotent self-selection of due households) is covered.
- **GAP-5 — Per-household timezone.** Send-time + equity windows use the single app TZ (E17); a second family
  in another timezone would see off-by-hours windows. *Acceptable + flagged:* same-region assumption holds for
  the current families; per-household TZ is the schema-cheap v1.2 upgrade, not foreclosed.
