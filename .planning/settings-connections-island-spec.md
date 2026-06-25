# Spec (first pass) — Settings island B: Connections → Svelte island (strangler)

**Quest:** Spine `b60c13cb-c5b3-4d7d-ac59-3d873ac0675d` (campaign "Family Coordination App"). Cluster **B** of the settings strangler (sequenced after A).
**Decisions (this session, 2026-06-25):** spec-first focused; parity-first; **single-route island** (`/settings/connections` only); flag `SETTINGS_CONNECTIONS_USE_ISLAND` (or reuse a shared settings flag — see Open items); prefix `con-`, port 5179.
**Status:** first-pass spec — **for the cross-plan review session** to harden alongside A and C. Open questions flagged inline.
**Template:** mirrors the **dashboard island** (single-route, single root) for the scaffold; backend mirrors `RecipesEndpoints.cs` (thin `/api` over the existing service, M1).

---

## 1. Goal & scope

Replace `Connections.razor` (498L, `@rendermode InteractiveServer`) — the household-connection invite flow — with a Svelte island over plain HTTP/JSON. **Behavior parity, no new UX.** The backend is thin: `IHouseholdConnectionService` already exposes every operation; the work is **the client-side state machine**.

### In scope (parity, all over the existing `IHouseholdConnectionService`)
- **Share section:** generate an invite code (`GenerateInviteAsync`) → display the 6-char code + expiry text + Copy-to-clipboard + Cancel (`InvalidateInviteAsync`). `Connections.razor:32-82,305-348`.
- **Enter-a-code section:** input (auto-uppercase, strip non-alphanumeric, max 6, `:361-370`) → Submit (`ValidateInviteCodeAsync`) → on valid, a **pre-connection confirmation** ("Connecting with X lets both families browse…") → Connect (`AcceptInviteAsync`) or Cancel. Error messages mapped from service error codes (`MapValidationError`, `:403-415`). `:84-151,372-457`.
- **Connected families section:** list (`GetConnectedHouseholdsAsync`) with connected-date + "Stop Sharing" → a **disconnect confirmation dialog** (3 bullet consequences) → `DisconnectHouseholdsAsync`. `:153-220,459-496`.

### Out of scope
- New UX. The invite ceremony is preserved exactly.
- Liveness/real-time (parity — no poll today).
- The recipes island's connected-household *browsing* (`/api/recipes/connected/*`) — separate, already shipped; this island only manages the *connections themselves*.

---

## 2. Endpoint surface — `/api/settings/connections` (thin, over `IHouseholdConnectionService`)

New group in `Endpoints/SettingsEndpoints.cs` (or its own file), `.RequireAuthorization().DisableAntiforgery()`, `(HouseholdId, UserId)` via `UserContextResolver` (M1).

| # | Method & route | Body | Returns | Delegates to |
|---|---|---|---|---|
| 1 | `GET /` | — | `ConnectionsDto` | `GetActiveInviteAsync` + `GetConnectedHouseholdsAsync` in one payload (parity `LoadData`). |
| 2 | `POST /invite` | — | `InviteDto` (201) | `GenerateInviteAsync(hh, uid)` → `{ code, expiresAt }`. |
| 3 | `DELETE /invite` | — | 204 | `InvalidateInviteAsync(hh)`. |
| 4 | `POST /validate` | `{ code }` | `ValidateResultDto` | `ValidateInviteCodeAsync(code, hh)` → `{ isValid, householdName, error }` (error is a stable code string the TS maps to copy). |
| 5 | `POST /accept` | `{ code }` | `AcceptResultDto` | `AcceptInviteAsync(code, hh, uid)` → `{ success, connectedHouseholdName, error }`. |
| 6 | `DELETE /connected/{householdId:int}` | — | 204 | `DisconnectHouseholdsAsync(hh, householdId)`. Validate the pairing is the caller's (the service is symmetric; M1 — only disconnect a pairing involving `hh`). |

```csharp
public sealed record ValidateRequest(string Code);
public sealed record AcceptRequest(string Code);
```

> **Validate/accept return a 200 with an outcome envelope** (not 4xx) because "invalid code" is an expected user-flow result, not an error — mirrors the service's `(bool, …, error)` tuples and keeps the island's error-mapping client-side. (The review should confirm this vs. a 422 convention.)

---

## 3. DTO contract (M9)

New `Services/Dtos/SettingsConnectionDtos.cs`. `ExpiresAt`/`ConnectedAt` are `DateTime` (UTC) → emit ISO; the island formats relative/absolute text client-side (port `GetExpiryText` `:350-357` + the connected-date format). No enums.

```csharp
public sealed record ConnectionsDto(InviteDto? ActiveInvite, IReadOnlyList<ConnectedDto> Connected);
public sealed record InviteDto(string Code, DateTime ExpiresAt);
public sealed record ConnectedDto(int HouseholdId, string HouseholdName, DateTime ConnectedAt);
public sealed record ValidateResultDto(bool IsValid, string? HouseholdName, string? Error);
public sealed record AcceptResultDto(bool Success, string? ConnectedHouseholdName, string? Error);
```

**TS mirror** + a `mapValidationError(code)` port of `:403-415` (codes: `self_connection`, `already_connected`, `expired`, `invalid`/`not_found`). Contract test + fixture for `ConnectionsDto` (mirror `DashboardDtoContractTests`).

---

## 4. Island structure — `frontend/connections/` (prefix `con-`, single root `connections-root`)

Copy the **dashboard** scaffold (single-route, single root, `untrack` load); **no `svelte-dnd-action`, no liveness**.
- **`connectionsStore.svelte.ts`** — `activeInvite`/`connected = $state`, plus the **flow state**: `enteredCode`, `validating`, `pendingHouseholdName`, `showConfirm`, `accepting`, `codeError`, `generating`, `disconnecting`, `#seq`. Methods: `load()`, `generate()`, `cancelInvite()`, `validate(code)`, `accept()`, `cancelConfirm()`, `confirmDisconnect(id)`/`disconnect()`. All `await`-then-update; `loadSeq` guard on `load()`.
- **`App.svelte`** — `untrack` one-time `load()`; the three sections (Share / Enter-a-code / Connected) + the disconnect confirm dialog + Toasts. Clipboard via `navigator.clipboard.writeText` directly (no Blazor JS interop).
- Components: `InviteShare`, `CodeEntry` (uppercase/strip/maxlen input + confirm step), `ConnectedList`, `ConfirmDialog`, `Toasts`. Re-scope `con-`.
- `dates.ts` — port `GetExpiryText` (relative "in N minutes/hours", absolute fallback) + connected-date format (noon-UTC, MN4).

---

## 5. Host + wiring
- `Connections.razor` → thin host (flag), `ConnectionsBlazor.razor` verbatim fallback. (Note: today it's `@rendermode InteractiveServer` explicitly — the host keeps that; the global rendermode covers it.)
- `CopyConnectionsIsland` target, `connections-node-build` Dockerfile stage, `docker-build.sh` entry, flag in compose + `.env`. `MapSettingsConnectionsEndpoints` in `Program.cs` (`IHouseholdConnectionService` already registered).

## 6. Tests
- Contract: `ConnectionsDtoContractTests` + fixture.
- Endpoint integration (real PG, two households): generate→get→validate (valid + self + already-connected + bad code) → accept → connected reflects it → disconnect → gone. Cross-household M1 (can't disconnect a pairing you're not in). 401 gate.

## 7. Build sequence
WP-1 backend (DTOs + endpoints) → WP-2 tests → WP-3 scaffold → WP-4 the flow (store + components + state machine) → WP-5 host+wiring → WP-6 `:8080` verify (full ceremony, two browser sessions to exercise accept) + loop-check.

## 8. Open items — RESOLVED (plan-review session, 2026-06-24)
- **Validate/accept envelope.** RESOLVED: **200-with-outcome** (not 422). An invalid/expired/self/already-connected code is an expected user-flow result rendered inline as a **warning** today, not an HTTP error. The endpoints return `{ isValid, householdName, error }` / `{ success, connectedName, error }`; the island maps the stable `error` code to copy (port of `MapValidationError`). Keeps api.ts on the happy path.
- **Flag granularity.** RESOLVED: per-cluster **`SETTINGS_CONNECTIONS_USE_ISLAND`** (see X1).
- **Clipboard fallback.** RESOLVED: keep today's try/catch → "Unable to copy — please select the code manually" (warning). Secure-context holds (localhost-as-secure; prod is https).
- **Two-session verify.** RESOLVED approach: seed a second household + an active invite via `psql` against the `:8080` PG, then drive accept from the primary auth session; assert connected-list reflects it and disconnect removes it. (Build-session detail.)

---

## 9. Review resolutions (plan-review session, 2026-06-24)

*See the cross-plan memo `D:/Development/data/outputs/reviews/settings-strangler-plan-review/`.*

**Cross-cutting (apply to B):**
- **X5 — Dates:** `Invite.ExpiresAt` and `Connected.ConnectedAt` are **full instants** — emit ISO-8601 (UTC, `Z`) and render with `new Date(iso)` in local tz (relative for expiry via `GetExpiryText` port, absolute for connected-date). **Do NOT** noon-UTC these (§3's "noon-UTC, MN4" note is wrong for instants — noon-UTC is only for bare date-only values). Update §3/§4.
- **X1/X2** as above; endpoints in their own `SettingsConnectionsEndpoints.cs` (own `Map…` + `Program.cs` registration), route namespace `/api/settings/connections`.

**Cluster-B hardening (verified against `Connections.razor`):**
- **R-B1 — Entity field name:** the invite entity exposes **`InviteCode`** (not `Code`) and `ExpiresAt`. The projection maps `InviteCode → code`; don't reference a non-existent `.Code`.
- **R-B2 — Disconnect M1 + idempotency:** `DELETE /connected/{householdId}` → `DisconnectHouseholdsAsync(callerHh, householdId)`. M1 holds because one arg is always the server-resolved caller household (a caller can only affect a pairing involving their own household). Confirm the service no-ops to **204** if the pairing is already gone (double-click safe). M1 test: a third household cannot disconnect a pairing it isn't part of.
- **R-B3 — Accept-failure state:** on accept failure, today returns to the **entry** view with the mapped error (hides the confirm step). The island state machine must replicate (error shows on entry, not confirm).
- **R-B4 — 4xx bodies** non-empty; 401 via `UserContextResolver` null → `Results.Unauthorized()`.

**Verdict: build-ready.** Thin backend; the risk is the client state machine — port the validate→confirm→accept / disconnect-confirm flows exactly. Build after A.
