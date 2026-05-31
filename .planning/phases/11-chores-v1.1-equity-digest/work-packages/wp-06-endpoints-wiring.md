# WP-06: Endpoints + Program.cs wiring (equity / settings / run-trigger / backfill)

**Wave:** 5 · **Execution:** review-needed *(auth + the one consolidated Program.cs edit)* · **Depends on:** WP-02, WP-03, WP-04, WP-05

## Precondition
Equity calculator + DTO (WP-02), settings service (WP-03), digest service (WP-05), builder/sender (WP-04)
exist. `ChoresEndpoints.cs` + `UserContextResolver` + the camelCase JSON options are in place (v1.0).

## Goal
Wire the HTTP surface and the single consolidated `Program.cs` edit: equity GET, digest-settings GET/PUT,
the cron-triggered run endpoint (shared-secret token, no cookie), the backfill action, the named
`"DiscordWebhook"` `HttpClient`, and DI for the new services.

## Files
- **Modify** `src/FamilyCoordinationApp/Data/Entities/ChoreEnums.cs` — add `ChoreLens.Equity = "equity"` to
  the `ChoreLens` constants **and** include it in `ChoreLens.All` (this WP **owns** the backend lens-id edit;
  the `/me/default-view` allowlist uses `ChoreLens.All`, so adding it here lets a user default onto Equity —
  M16). WP-09 mirrors it in the island `types.ts`/`CHORE_LENSES`.
- **Modify** `src/FamilyCoordinationApp/Endpoints/ChoresEndpoints.cs` (add `GET /equity`, `GET/PUT
  /digest-settings`, `POST /digest/run`, `POST /seed-starter`; request/response records)
- **Modify** `src/FamilyCoordinationApp/Program.cs` (DI: `IDigestSettingsService`, `IDigestService`,
  `DigestBuilder`, `IDigestSender`→`DiscordWebhookDigestSender`, `ChoreEquityCalculator` **as a singleton**
  (mirroring `AddSingleton<ChoreStatusCalculator>()` — it's pure/stateless); the named `"DiscordWebhook"`
  `HttpClient` + `AddStandardResilienceHandler`; nothing else)
- **Modify** `.env.example` (add `CHORES_DIGEST_TRIGGER_TOKEN`; document the cron line)
- **Create** `tests/FamilyCoordinationApp.Tests/Services/DigestEndpointWiringTests.cs` (token-check unit via an extracted `internal static` validator; equity window allowlist)

## Implementation notes
- **Equity:** `group.MapGet("/equity", GetEquity)` on the existing cookie-authed `/api/chores` group. Parse
  `window` (default `week`; allowlist `{week, all}`; else 400). Resolve user (M1); fetch the household's
  `ChoreCompletion`s + members (`MemberDto`s, the same build the board uses) + active chores;
  `ChoreEquityCalculator.Compute(...)` with `TimeProvider` now + injected `TimeZoneInfo` for the distribution
  (window=`week` ⇒ **Monday-start**, WP-02); compute `fallingBehindCount` + `upForGrabsCount` over the active
  chores via `ChoreStatusCalculator.Compute` + the **shared `ChoreAttention` predicates** (WP-05's
  `ChoreAttention.IsFallingBehind`/`IsUpForGrabs`) — the SAME predicates the digest uses, so lens + digest
  never diverge (council MAJOR). Map `ChoreEquityResult`→`ChoreEquityDto` (incl. those two counts — WP-02
  DTO). Reuse `ChoreBoardService`'s chore/member fetch where cleanly available rather than duplicating the
  query (if its dueness helpers are `private`, the active-chore dueness here goes through `ChoreStatus
  Calculator` + `ChoreAttention`, not a copy of the private board logic).
- **Settings:** `GET /digest-settings` → safe `DigestSettingsView` (no URL); `PUT /digest-settings` →
  `UpdateAsync`; validation → 400. Cookie-authed, household-scoped. **Freeze the wire contract (council
  MAJOR — casing + clear-sentinel):**
  - Enums serialize via the global `JsonStringEnumConverter(CamelCase)`, so `cadence` is **`"weekly"`** and
    `sendDayOfWeek` is **`"sunday"|"monday"|…|"saturday"`** (lowercase) on the wire — NOT `"Weekly"`/`"Sunday"`.
    The island `types.ts` (WP-11) and any settings fixture must use these lowercase strings.
  - Request body `{ enabled:bool, cadence:"weekly", sendDayOfWeek:"sunday"…, sendHourLocal:int(0-23),
    webhookUrl: string|null }` where **`webhookUrl` semantics are frozen**: **omitted/undefined ⇒ leave
    unchanged**; **non-blank string ⇒ replace (encrypt)**; **explicit `null` or empty `""` ⇒ clear** (set
    `WebhookUrlProtected = null`). WP-03 + WP-11 use this identical rule.
  - Add a tiny `digest-settings` DTO contract assertion (in `DigestEndpointWiringTests`) pinning the casing of
    `cadence`/`sendDayOfWeek` so it can't silently drift from `types.ts`.
- **Run trigger (E8/E9):** map `MapPost("/api/chores/digest/run", RunDigests)` on the **top-level
  `IEndpointRouteBuilder app`** param of `MapChoresEndpoints` — **NOT** on the `group` variable (the
  `/api/chores` group carries `.RequireAuthorization()`; mapping on it would force cookie auth and break the
  cron-token design — council). It gets **no** `.RequireAuthorization()`. Read `CHORES_DIGEST_TRIGGER_TOKEN`
  from `IConfiguration`. If unconfigured → return a **non-empty-body** 503 (`Results.Json(new { error =
  "digest trigger disabled" }, statusCode: 503)`). Require header `X-Digest-Trigger-Token`; compare with
  `CryptographicOperations.FixedTimeEquals` (UTF-8 bytes); mismatch/missing → a **non-empty-body** 401
  (`Results.Json(new { error = "unauthorized" }, statusCode: 401)`). On success → `IDigestService.
  RunDueAsync()` → 200 `{ sent, skipped, failed }`. Reject a token supplied via query string. `DisableAntiforgery`.
  **NB (v1.0 WP-08 quirk):** the app-global `UseStatusCodePagesWithReExecute("/not-found")` rewrites
  **empty-body** error responses through the Blazor page — so the 401/503 here MUST carry a JSON body (above),
  otherwise cron sees a rewritten 400/200-shaped page. Verify by booting the host (WP-08), not just `dotnet build`.
- **Backfill (E15):** `POST /seed-starter` (cookie-authed). `SeedData.SeedChoresAndRoomsAsync` returns `Task`
  (void) and is internally idempotent, so to report `{ seeded:bool }` the handler **probes first**:
  `var alreadyHad = await context.Rooms.AnyAsync(r => r.HouseholdId == householdId, ct) || await
  context.Chores.AnyAsync(c => c.HouseholdId == householdId, ct);` (predicate lambdas — there is no
  `Any(int)` overload) **before** calling; `seeded = !alreadyHad`; call the seed; return 200 `{ seeded }`.
  Household-scoped (M1). (Do not change the `SeedChoresAndRoomsAsync` signature.)
- **Program.cs (scope-fenced, E2):** add the new `AddScoped`/`AddSingleton` lines next to the v1.0 chore DI
  block (`AddScoped<IDigestSettingsService,DigestSettingsService>()`, `AddScoped<IDigestService,DigestService>
  ()`, `AddSingleton<ChoreEquityCalculator>()`, `AddSingleton<DigestBuilder>()`, `AddScoped<IDigestSender,
  DiscordWebhookDigestSender>()`). Add the named client, **filling every property** (no ellipsis — mirror the
  real `RecipeScraper` block at `Program.cs:142-157`, which uses these exact names from
  `Microsoft.Extensions.Http.Resilience`):
  ```csharp
  builder.Services.AddHttpClient("DiscordWebhook")
      .AddStandardResilienceHandler(o =>
      {
          o.Retry.MaxRetryAttempts = 3;
          o.Retry.BackoffType = DelayBackoffType.Exponential;
          o.Retry.UseJitter = true;
          o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
          o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
          o.CircuitBreaker.FailureRatio = 0.5;
          o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60); // must be >= 2 * AttemptTimeout
          o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
      });
  ```
  (`DelayBackoffType` is `Polly.DelayBackoffType`; `using Polly;` is already a **file-level global** at the top
  of `Program.cs` — no new `using` needed.) **No other Program.cs change.**
- **Map-with-body footgun (v1.0 WP-06 bug):** any `MapPost`/`MapDelete` handler taking a body record must
  ensure the body is inferred correctly (annotate `[FromBody]` where the host-build inference complains);
  **boot the host** (the WP-08 harness does) — don't rely on `dotnet build` alone.

## Verification (V3, V7 partial, V12, V13, V14)
- `dotnet build` + `dotnet format --verify-no-changes` (changed files) + `dotnet test --filter
  "kind!=integration"` green. Wiring unit tests: token validator (valid/invalid/unconfigured), equity window
  allowlist. The full HTTP/auth/idempotency assertions are WP-08 (real Postgres + booted host).

## Failure criteria
- Run endpoint reachable without a valid token, or token via query string, or runs when unconfigured. ·
  Equity/settings/backfill not household-scoped (M1). · `Program.cs` edits beyond DI+HttpClient+endpoint-map. ·
  Host fails to boot (map-with-body footgun). · Webhook returned/logged.

## Boundary
Endpoints + the single Program.cs edit + `.env.example` ONLY. Do not touch shopping-list endpoints/island/
Docker (MN6). Do not modify the entity/services (upstream WPs own them). No island code (WP-09+).

## Notes for downstream
- **WP-08** gets the exact wire contract here (routes, bodies, the `X-Digest-Trigger-Token` header, status
  codes). **WP-09/11** get the equity + settings request/response shapes for `api.ts`. The run endpoint is the
  only chore route not behind cookie auth.
