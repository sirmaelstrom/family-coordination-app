# WP-08: Integration tests (digest-run, equity, settings, backfill on real Postgres)

**Wave:** 6 · **Execution:** review-needed · **Depends on:** WP-06

## Precondition
The WP-08-v1.0 harness exists: `PostgresContainerFixture` (per-class DB on a shared container),
`ChoresWebAppFactory` (`X-Test-User` `TestAuthHandler`, seeded Household A/B + users, `CreateClientAs` /
`CreateAnonymousClient`), `[Trait("kind","integration")]`, `[Collection("chores-integration")]`. The v1.1 wire
contract is frozen by WP-06.

## Goal
Cover the new seams on **real Postgres + the booted host**: trigger-token auth, digest idempotency + failure
isolation + multi-tenant isolation, equity isolation, settings encryption-over-the-wire, backfill idempotency,
and that the new migration applies clean.

## Files
- **Create** `tests/FamilyCoordinationApp.Tests/Integration/DigestRunIntegrationTests.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Integration/EquityEndpointIntegrationTests.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Integration/DigestSettingsIntegrationTests.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Integration/BackfillIntegrationTests.cs`
- **Modify** `ChoresWebAppFactory.cs` if needed: bind `IDigestSender`→a capturing fake in the test host;
  configure `CHORES_DIGEST_TRIGGER_TOKEN` for the host; seed two households with digest settings due now.

## Implementation notes
- **Determinism (avoid flaky time-dependent tests):** the run endpoint calls `RunDueAsync()` which reads the
  injected `TimeProvider`. In the test host, **override `TimeProvider` with a fixed instant** (register a fake
  `TimeProvider` in `ConfigureTestServices`, replacing `TimeProvider.System`) so "now" is known, then seed the
  two households' `HouseholdChoreDigestSettings` with `SendDayOfWeek`/`SendHourLocal` that make them **due at
  that fixed now** (in the app `TimeZoneInfo`). Do NOT seed against the wall clock. The not-due case seeds a
  different day/hour. (If overriding `TimeProvider` proves awkward through `WebApplicationFactory`, the
  fallback is to compute the settings from `TimeProvider.System` at seed time AND assert against the same
  clock — but the fixed-provider approach is preferred and non-flaky.)
- **Seeding the new rows:** `ChoresWebAppFactory`'s existing seed is guarded (`if Households.Any() return`,
  ~line 130); insert the two households' digest-settings rows + the multi-member completions the
  equity-isolation assertion needs **inside that same guarded block** (test infra — permitted), NOT as a
  separate conditional after it (a separate guard would never run on a DB that already has households). Bind `IDigestSender`→a capturing fake in the
  test host (replacing `DiscordWebhookDigestSender`) so no real network call occurs.
- **Token auth (E9):** `POST /api/chores/digest/run` with a valid `X-Digest-Trigger-Token` → 200; missing →
  401; wrong → 401; token in query string → ignored (still 401). A host configured WITHOUT the token →
  503/disabled. (Configure two host variants or override config per test.) **Assert the 401/503 carry a JSON
  body** (the `UseStatusCodePagesWithReExecute` empty-body→rewrite quirk — WP-06 returns `Results.Json(...,
  statusCode)`); a bare `StatusCode(401)` with no body would be rewritten and this test would catch it.
- **Idempotency (E10/M10):** seed two households due now, each with a webhook + the fake sender bound. POST
  /run, then POST /run again within the window → the fake recorded **exactly one** send per household across
  both calls; `LastSentAt` set. A not-due/disabled household: zero sends.
- **Concurrent double-fire (council C2 — the real cron failure mode):** issue **two `POST /api/chores/digest/run`
  calls concurrently** (`Task.WhenAll`) at the same fixed instant → assert the fake sender recorded **exactly
  one** send per household total (the atomic `ExecuteUpdateAsync` claim, WP-05, must serialize them). This is
  the test that proves the read-send-stamp race is closed; without WP-05's atomic claim it fails.
- **Multi-tenant isolation:** each household's captured `DigestModel` reflects only its own completions
  (distinct seeded data per household; assert no cross-bleed).
- **Failure isolation:** bind a fake sender that throws for **one household, distinguished by its seeded
  webhook URL or its `DigestModel` headline/household-name** (NOT by householdId — `IDigestSender.SendAsync`
  only receives `webhookUrl` + `DigestModel`, council). Household B still sends; summary `failed>=1, sent>=1`;
  and (per WP-05 compensate) the failed household's `LastSentAt` is restored so a later run could retry.
- **Equity (V3):** `GET /api/chores/equity?window=week` as UserA → 200, sums only Household A; as UserB → only
  Household B; `window=bogus` → 400; anonymous → rejected (assert 4xx + no leak, accommodating the
  `UseStatusCodePagesWithReExecute` empty-404→400 quirk).
- **Settings (V4):** PUT a webhook as UserA; GET → `hasWebhook:true`, **URL absent** from the response body;
  read the raw row via `PostgresDbContextFactory` → `WebhookUrlProtected` ≠ plaintext.
- **Backfill (V9):** a fresh household (insert directly, not via `CreateHouseholdAsync` to avoid auto-seed) →
  POST /seed-starter → rooms/chores created; second POST → no-op (counts equal); scoped to caller.
- **Migration (V11):** host boots via `MigrateAsync` against a fresh container (already the harness path) →
  green proves `AddChoreDigestSettings` applies on the v1.0 chain.

## Verification (V3, V4, V7, V9, V11)
- Full suite WITH Docker green (`dotnet test`); `--filter "kind!=integration"` still green WITHOUT Docker
  (trait isolation intact). New files `dotnet format`-clean.

## Failure criteria
- Any test makes a real discord.com call (must use the bound fake). · A skipped/`[Fact(Skip)]` left in. ·
  Idempotency/isolation assertions weakened to "any 4xx" where a precise code is checkable. · Unit suite
  pulled into needing Docker.

## Boundary
Tests + minimal `ChoresWebAppFactory` test-host wiring ONLY. No production code change (if a real bug is
found, document + escalate per E6 — don't patch inside this WP).

## Notes for downstream
- Confirms the digest seam end-to-end without sending to Discord (GAP-1 stays the only manual link). Reuses
  the v1.0 fixture identities; adds digest-settings + multi-member completion seeding to the factory.
