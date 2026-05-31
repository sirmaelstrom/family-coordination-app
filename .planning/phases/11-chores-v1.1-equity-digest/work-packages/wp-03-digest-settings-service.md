# WP-03: DigestSettingsService (CRUD + webhook encryption)

**Wave:** 2 · **Execution:** review-needed *(secret handling)* · **Depends on:** WP-01

## Precondition
WP-01's `HouseholdChoreDigestSettings` entity + DbSet exist. Data Protection is registered
(`Program.cs:24-59`); `IDataProtectionProvider` is resolvable. No stored value is encrypted yet (new use).

## Goal
A scoped service that reads/writes a household's digest settings, encrypting the webhook URL at rest and
never exposing the plaintext.

## Files
- **Create** `src/FamilyCoordinationApp/Services/Interfaces/IDigestSettingsService.cs`
- **Create** `src/FamilyCoordinationApp/Services/DigestSettingsService.cs`
- **Create** `tests/FamilyCoordinationApp.Tests/Services/DigestSettingsServiceTests.cs`

## Implementation notes
- Primary ctor `(IDbContextFactory<ApplicationDbContext> dbFactory, IDataProtectionProvider dpProvider,
  TimeProvider timeProvider, ILogger<DigestSettingsService> logger)`. Protector:
  `dpProvider.CreateProtector("ChoreDigestWebhook")`.
- API:
  - `Task<DigestSettingsView> GetAsync(int householdId, ct)` → returns a **safe** view: `{ enabled, cadence,
    sendDayOfWeek, sendHourLocal, hasWebhook (bool), webhookHint (string? masked, e.g. last 4 of the URL path
    or null), lastSentAt }`. **Never returns the plaintext or ciphertext URL.** Returns defaults if no row.
  - `Task UpdateAsync(int householdId, DigestSettingsUpdate update, ct)` → upserts the row; if
    `update.WebhookUrl` is a **non-blank string**, `Protect` it into `WebhookUrlProtected`; if it is
    **explicit `null` or empty `""`**, set `WebhookUrlProtected = null` (clear); if **omitted/undefined**,
    leave it unchanged (frozen contract — identical wording in WP-06/WP-11; use a tri-state so "omitted" is
    distinguishable from "null", e.g. a sentinel or a `bool WebhookProvided` flag on the update record).
    Validate `SendHourLocal ∈ [0,23]`, `SendDayOfWeek` valid, `Cadence` valid (else throw a validation
    exception → 400 at the endpoint). Sets `UpdatedAt`/`CreatedAt`.
  - `Task<string?> GetDecryptedWebhookAsync(int householdId, ct)` → `Unprotect`s for the digest sender
    (WP-05 only). Returns null if no webhook. A normal **public** method on the interface, named + XML-doc'd
    as send-only (do NOT make it an `internal` interface member — keep the interface conventional; the
    send-only discipline is convention + the doc comment, enforced by the fact that only `DigestService`
    calls it). It must **never** be wired into any cookie-authed read path that returns to a client.
- **Never log** the plaintext or ciphertext webhook (MN7). On `Unprotect` failure (key rotation), log a
  warning WITHOUT the value and return null (the household is treated as unconfigured that run).

## Verification (V4)
- Unit: `Update` then read the raw entity → `WebhookUrlProtected != plaintext` (ciphertext); a
  `GetDecryptedWebhookAsync` round-trips to the original. `GetAsync` returns `hasWebhook:true` + no URL.
  Invalid `SendHourLocal`/cadence → validation exception. Unit suite green; format clean.

## Failure criteria
- Webhook stored in plaintext. · Plaintext/ciphertext URL returned from `GetAsync` or logged. · `Unprotect`
  failure throws/crashes the read. · Missing `HouseholdId` filter (M1).

## Boundary
Service + interface + tests ONLY. No endpoint (WP-06), no `Program.cs` DI, no sender. Does not read/aggregate
completions (that's equity).

## Notes for downstream
- WP-06 registers `AddScoped<IDigestSettingsService, DigestSettingsService>()` and maps the settings endpoints
  to `GetAsync`/`UpdateAsync`. WP-05 calls `GetDecryptedWebhookAsync` at send time only. The masked
  `webhookHint` is what the island settings surface shows (WP-11) — never the URL.
