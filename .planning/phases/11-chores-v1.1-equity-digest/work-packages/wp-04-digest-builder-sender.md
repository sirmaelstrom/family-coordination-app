# WP-04: DigestBuilder + DigestModel + sender (Discord + fake)

**Wave:** 3 · **Execution:** review-needed *(non-punitive framing)* · **Depends on:** WP-02

## Precondition
WP-02's `ChoreEquityCalculator` + `ChoreEquityResult` exist; `ChoreStatusCalculator` (v1.0) computes
dueness. The named `"DiscordWebhook"` `HttpClient` will be registered in WP-06 (this WP resolves it via
`IHttpClientFactory`). **WP-02's frozen result types (restated verbatim — you cannot see WP-02):**
```csharp
public record ChoreEquityResult(int TotalPoints, int TotalCompletions, double EqualSharePct,
    IReadOnlyList<MemberEquityShare> Members);
public record MemberEquityShare(int UserId, string DisplayName, string Initials, string? PictureUrl,
    int Points, int Completions, double SharePct);   // SharePct is PERCENT 0..100
```

## Goal
A pure `DigestBuilder` that turns a per-household snapshot into a `DigestModel`, plus the `IDigestSender`
boundary with a Discord webhook implementation and a fake for tests. No live sends in tests.

## Files
- **Create** `src/FamilyCoordinationApp/Services/Digest/DigestModel.cs` (`DigestModel`, `DigestMemberLine`)
- **Create** `src/FamilyCoordinationApp/Services/Digest/DigestBuilder.cs` (pure)
- **Create** `src/FamilyCoordinationApp/Services/Digest/IDigestSender.cs`
- **Create** `src/FamilyCoordinationApp/Services/Digest/DiscordWebhookDigestSender.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Services/DigestBuilderTests.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Services/DiscordWebhookDigestSenderTests.cs` (stub `HttpMessageHandler`)
- **Create** `tests/FamilyCoordinationApp.Tests/Fakes/FakeDigestSender.cs` (records invocations)
- **Create** `tests/FamilyCoordinationApp.Tests/Fixtures/Digest/discord-payload.json` (expected embed shape)

## Implementation notes
- `DigestModel`: `{ string CollectiveHeadline; int TotalCompletions; int TotalPoints; IReadOnlyList<DigestMember
  Line> Distribution; IReadOnlyList<string> FallingBehind; int UpForGrabsCount }`. `DigestMemberLine`:
  `{ string DisplayName; int Points; double SharePct }` — **no userId-as-mention field** (M11/MN8).
- **Named input record (council — no anonymous tuple in a public contract):**
  `public record DigestChoreLine(string Name, DueState DueState);` (namespace `FamilyCoordinationApp.Services.
  Digest`). WP-05 builds `IReadOnlyList<DigestChoreLine>` from the household's active chores.
- `DigestBuilder` is **stateless with a parameterless ctor (no DI / no injected services)** — so WP-06 can
  register it `AddSingleton<DigestBuilder>()` safely (no captive-dependency risk). `DigestBuilder.Build(
  string householdName, ChoreEquityResult equity, IReadOnlyList<DigestChoreLine> choreDueness, int
  upForGrabsCount)` → `DigestModel`. Pure (no I/O). Headline is collective + non-punitive
  ("The house knocked out {n} chores ({p} pts) this week 💪"). FallingBehind = `Name`s of `choreDueness`
  entries whose `DueState` is `Overdue` or `DueToday`. Distribution = `equity.Members` mapped to
  `DigestMemberLine` (`DisplayName`/`Points`/`SharePct`); neutral order (stable by name, or points-desc as a
  *distribution* — never framed as a ranking/leaderboard in copy).
- `IDigestSender`: `Task SendAsync(string webhookUrl, DigestModel model, CancellationToken ct)`.
- `DiscordWebhookDigestSender(IHttpClientFactory httpFactory, ILogger<...> logger)`: resolves
  `CreateClient("DiscordWebhook")`; renders the model to a Discord webhook payload — an **embed**
  (title=headline, fields=distribution/falling-behind/up-for-grabs) — and POSTs JSON. Set
  `allowed_mentions = { parse: [] }` to suppress ALL pings (defense-in-depth, M11). Surface a non-2xx as an
  exception (so the resilience handler retries / the orchestrator logs+isolates). **Never log the webhook URL.**
- `DiscordWebhookDigestSenderTests`: inject a stub `HttpMessageHandler` capturing the request; assert the
  payload matches `discord-payload.json` (parsed-node), assert `allowed_mentions.parse == []` and no
  `@`-mention in `content`; assert a 429/500 from the stub is surfaced (not swallowed). **Zero real network.**

## Verification (V5, V6)
- `DigestBuilderTests`: model totals + distribution match a fixture household; assert the model has **no**
  mention/targeting field and no ranking flag (non-punitive). · Sender tests pass against the stub only. ·
  Unit suite green; format clean.

## Failure criteria
- Any `@mention`/targeted-ping in the payload, or `allowed_mentions` not suppressing pings. · Live network
  call to discord.com in any test. · Webhook URL logged. · `DigestBuilder` doing I/O (not pure).

## Boundary
Builder + model + sender + fake + tests ONLY. No `Program.cs` (the `HttpClient` registration is WP-06), no
DB access (the builder takes a prepared snapshot; WP-05 assembles it), no endpoint.

## Notes for downstream
- WP-05's `DigestService` assembles the snapshot (equity + dueness + up-for-grabs) per household and calls
  `IDigestSender.SendAsync(decryptedWebhook, model, ct)`. WP-08 uses `FakeDigestSender` to assert
  idempotency/isolation. WP-06 registers the named `"DiscordWebhook"` `HttpClient` + binds `IDigestSender`→
  `DiscordWebhookDigestSender` in prod (tests bind the fake).
