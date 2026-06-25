# Spec (first pass) — Settings island C: Admin (Household requests + Feedback) → Svelte island (strangler)

**Quest:** Spine `fd629f11-ff18-4539-ace1-6bda47b72fe2` (campaign "Family Coordination App"). Cluster **C** of the settings strangler (sequenced last — most backend net-new).
**Decisions (this session, 2026-06-25):** spec-first focused; parity-first; **one island, two routes** (`/settings/households` + `/settings/feedback`) via the recipes data-view pattern; prefix `adm-`, port 5180.
**Status:** first-pass spec — **for the cross-plan review session** to harden alongside A and B. Open questions flagged inline — this cluster has the most net-new backend + a **new auth shape**, so it most needs the review.
**Template:** scaffold mirrors recipes (two-route bundle) + dashboard; backend introduces **two new services** + a **site-admin authorization gate**.

---

## 1. Goal & scope

Replace the two **site-admin-leaning** settings pages with one Svelte island. Both are **direct-EF today (no service layer)** — the strangler must lift their logic into testable services. This is the heaviest cluster.

### In scope (parity)

**Household requests (`/settings/households`) — SITE-ADMIN ONLY** (`HouseholdAdmin.razor`, 353L, gated by `SiteAdminService.IsSiteAdmin`, `:19,184`):
- List requests (pending-first, then newest, `:196-199`) with a **pending/all** filter (`:157-161`).
- List existing households + member counts (`:201-204`).
- **Approve** a pending request → a multi-entity transaction: create `Household` + create whitelisted `User` + mark request Approved (ReviewedAt/ReviewedBy) + **seed default categories** (`SeedData.SeedDefaultCategoriesAsync`) + log (`:213-274`).
- **Reject** → reason dialog → mark Rejected + reason + ReviewedAt/ReviewedBy + log (`:276-317`).
- 30s auto-refresh poll (`:172-174`).

**Feedback (`/settings/feedback`) — DUAL-MODE** (`FeedbackAdmin.razor`, 326L; **not** site-admin-gated, but visibility differs, `:209-213`):
- Site admin sees **all** feedback; a regular user sees **only their household's** (`:178-180,210-212`).
- List (newest-first) with **all/unread/open** filter (`:133-138`); type chip (Bug/FeatureRequest/…), New/Resolved chips, author (or "Deleted user"), message, current-page link (`:38-107`).
- **Mark read** / **mark resolved** (sets read+resolved) / **reopen** (`:227-267`).
- 15s **smart** poll — reload only when the count changed (`:171-194`).

### Out of scope
- New UX. The `RejectReasonDialog` becomes an in-island dialog (parity).
- Changing who can do what — **preserve today's authorization exactly** (see the Feedback-mutation open question, §7).

---

## 2. Auth shape (the new bit — review focus)

This cluster breaks the "just `.RequireAuthorization()`" pattern of A/B:
- **Household-requests routes are site-admin-only.** The endpoint must check `ISiteAdminService.IsSiteAdmin(callerEmail)` → **403** otherwise (not just authenticated). Proposed: a small endpoint filter or a per-handler guard resolving the caller's email from claims.
- **Feedback routes are dual-mode.** Visibility (and possibly mutation — §7) depends on `IsSiteAdmin`: admin → all households; regular → own household only (M1-scoped). The GET must apply the household filter server-side for non-admins; a non-admin must never receive another household's feedback.
- The island needs to know `isSiteAdmin` to render the households view (or show "Access denied") and to scope feedback — carry it in a `GET /api/settings/admin/context` or fold an `isSiteAdmin` flag into each list response. (Review: pick one.)

---

## 3. Endpoint surface (two new services)

New `Endpoints/SettingsAdminEndpoints.cs`, `.RequireAuthorization().DisableAntiforgery()`, caller via `UserContextResolver` + claims email for the site-admin check.

### 3a. `/api/settings/household-requests` — **site-admin only** → new `IHouseholdRequestService`

| # | Method & route | Body | Returns | Notes |
|---|---|---|---|---|
| 1 | `GET /?filter=pending\|all` | — | `HouseholdRequestsDto` (requests + households) | 403 if not site-admin. Pending-first ordering (parity). |
| 2 | `POST /{id:int}/approve` | — | `HouseholdSummaryDto` (201) | The **transaction**: household+user+seed+status. Lift `:225-267` into `IHouseholdRequestService.ApproveAsync(id, reviewerEmail)` (testable; today it's inline EF). Already-reviewed ⇒ 409. |
| 3 | `POST /{id:int}/reject` | `{ reason }` | 204 | `RejectAsync(id, reason, reviewerEmail)`. Already-reviewed ⇒ 409. |

### 3b. `/api/settings/feedback` — **dual-mode** → new `IFeedbackService`

| # | Method & route | Body | Returns | Notes |
|---|---|---|---|---|
| 4 | `GET /?filter=all\|unread\|open` | — | `FeedbackListDto` | Admin → all; regular → own household (server-scoped, M1). Includes `isSiteAdmin` for the island. |
| 5 | `POST /{id:int}/read` | — | 204 | `MarkReadAsync`. Authorize: admin any; regular only own-household item ⇒ else 404 (no leak). |
| 6 | `POST /{id:int}/resolve` | — | 204 | `MarkResolvedAsync` (sets read+resolved). Same authz. |
| 7 | `POST /{id:int}/reopen` | — | 204 | `ReopenAsync`. Same authz. |

```csharp
public sealed record RejectRequest(string Reason);
```

---

## 4. DTO contract (M9)

New `Services/Dtos/SettingsAdminDtos.cs`. `HouseholdRequestStatus` + `FeedbackType` are **real enums** ⇒ camelCase via the global converter. Dates → ISO (format client-side, noon-UTC).

```csharp
public sealed record HouseholdRequestsDto(
    IReadOnlyList<HouseholdRequestDto> Requests, IReadOnlyList<HouseholdSummaryDto> Households);
public sealed record HouseholdRequestDto(
    int Id, string HouseholdName, string DisplayName, string Email,
    HouseholdRequestStatus Status, string RequestedAt,
    string? ReviewedAt, string? ReviewedBy, string? RejectionReason);
public sealed record HouseholdSummaryDto(int HouseholdId, string Name, int MemberCount, string CreatedAt);

public sealed record FeedbackListDto(bool IsSiteAdmin, IReadOnlyList<FeedbackDto> Items);
public sealed record FeedbackDto(
    int Id, FeedbackType Type, string Message, string? CurrentPage,
    bool IsRead, bool IsResolved, string CreatedAt,
    string? AuthorName, bool AuthorDeleted);   // AuthorName null + AuthorDeleted true ⇒ "Deleted user" (parity :67-70)
```

**TS mirror** + contract tests + fixtures for both list DTOs (mirror `DashboardDtoContractTests`).

---

## 5. Island structure — `frontend/admin/` (prefix `adm-`, roots `admin-households-root` / `admin-feedback-root`)

Copy the recipes scaffold (two-route bundle); **KEEP `liveness.ts`** — this is the one settings cluster that polls (households 30s, feedback 15s). Re-scope `adm-`.
- `HouseholdsApp.svelte` (view `households`): access-denied if `!isSiteAdmin`; pending/all filter; request cards with approve/reject (+ reason dialog); existing-households table; liveness 30s.
- `FeedbackApp.svelte` (view `feedback`): all/unread/open filter; feedback cards + the read/resolve/reopen menu; liveness 15s (smart: reconcile only refetches — the `loadSeq` guard makes the count-check optional, but porting "reload only if count changed" is a cheap parity win).
- Stores: `householdRequestsStore`, `feedbackStore` — `untrack` load + `loadSeq` guard; mutations await-then-reload.
- Components: `RequestCard`, `RejectReasonDialog`, `HouseholdsTable`, `FeedbackCard`, `ConfirmDialog`, `Toasts`.

## 6. Host + wiring
- `HouseholdAdmin.razor` + `FeedbackAdmin.razor` → thin hosts (flag) + verbatim Blazor fallbacks.
- `CopyAdminIsland`, `admin-node-build` Dockerfile stage, `docker-build.sh`, flag in compose + `.env`.
- `Program.cs`: register `IHouseholdRequestService` + `IFeedbackService`; `MapSettingsAdminEndpoints`. `ISiteAdminService` already registered.

## 7. Tests
- Contract: `HouseholdRequestsDtoContractTests` + `FeedbackListDtoContractTests` + fixtures.
- `IHouseholdRequestService` integration (real PG): **approve creates household+user+seeds categories+marks approved** (the load-bearing transaction); reject sets reason; double-review ⇒ 409.
- `IFeedbackService`: dual-mode visibility (admin all vs regular own-household), read/resolve/reopen, cross-household item ⇒ 404 for a non-admin.
- **Auth:** non-site-admin → 403 on every household-requests route; non-admin feedback GET returns only own household.

## 8. Open items — RESOLVED (plan-review session, 2026-06-24)
- **Feedback mutation authorization.** RESOLVED: **parity = allow-on-visible, but enforce "visible" server-side.** Endpoints 5-7 authorize: site-admin → any item; non-admin → only an item in their **own household**, else **404** (non-empty body, no existence leak). See **R-C1** — this is REQUIRED, not optional: the naive lift is an IDOR. The question "should feedback mgmt be admin-only at all?" is harvested as a separate provisional quest (don't change behavior in the strangler).
- **Site-admin signal.** RESOLVED: **no dedicated context endpoint.** Household-requests routes 403 for non-admins → the island renders "Access denied" on a 403 from its list GET (the 403 IS the signal). `FeedbackListDto.isSiteAdmin` already carries it for the feedback view. Drop the `/context` idea (R-C4).
- **Approve transaction boundary.** RESOLVED: wrap household+user+status+seed in ONE explicit transaction (R-C2). Confirmed atomicity gap is real.
- **Polling vs liveness.** RESOLVED: reuse the shared **`liveness.ts`** (visible-only), per-view intervals **30s (households) / 15s (feedback)**, keep feedback's count-check as a cheap bandwidth win (R-C9).
- **Flag granularity.** RESOLVED: per-cluster **`SETTINGS_ADMIN_USE_ISLAND`** (covers both C routes; see X1).

---

## 9. Review resolutions (plan-review session, 2026-06-24) — this cluster has the load-bearing findings

*See the cross-plan memo `D:/Development/data/outputs/reviews/settings-strangler-plan-review/`. The review-council was run on the set but produced no usable signal (opus lens crashed; gemini hallucinated an unrelated spec; gpt rubric-refused) — findings below are source-grounded, not council-derived.*

**Cross-cutting (apply to C):** X1 flag `SETTINGS_ADMIN_USE_ISLAND`; X2 own file `SettingsAdminEndpoints.cs`; **X5 dates** — `RequestedAt`/`ReviewedAt`/`Feedback.CreatedAt` are **full instants** (today rendered with time-of-day), emit ISO-8601 (UTC, `Z`), render local with `new Date(iso).toLocaleString()`. The §4 "noon-UTC" note is wrong for these instants — fix it.

**Cluster-C hardening:**
- **R-C1 — FEEDBACK MUTATION IDOR (must-fix; the circuit→REST lift introduces it).** Today `MarkAsRead/Resolved/Reopen` do `Feedbacks.FindAsync(id)` then flip flags with **NO household scoping** — safe ONLY because the Blazor list never renders other households' IDs to a non-admin. As a REST endpoint, a non-admin could POST an arbitrary `{id}` and mutate another household's feedback. **Endpoints 5-7 MUST enforce: admin → any; non-admin → own-household item only, else 404.** Not optional — shipping the naive lift is a vuln. Integration test: non-admin POST against another household's feedback id → 404, no mutation.
- **R-C2 — Approve transaction atomicity.** Today = **three separate commits**: (1) `SaveChanges` after `Households.Add`; (2) `SaveChanges` after adding User + setting request status; (3) separate `SeedData.SeedDefaultCategoriesAsync` on its own context. Mid-failure → orphan household w/ no user (req still pending), or household+user+approved but **no categories**. `ApproveAsync` must wrap all four in ONE `BeginTransactionAsync` (multiple SaveChanges inside the tx — the user FK needs `household.Id` so a single SaveChanges won't do — commit once). Test: a forced failure mid-approve rolls back fully.
- **R-C3 — Missing already-reviewed guard = latent duplicate-household bug.** Today approve/reject re-fetch and set status with **no check** that the request is still `Pending` (only the buttons + a 30s poll gate it). Two admins (or one on a 30s-stale view) can both approve → **two households for one request**. Endpoint #2/#3 must reject a non-Pending request with **409**. This is a real bug fix under the strangler (invisible to honest users) — confirm OK with operator.
- **R-C4 — Site-admin signal:** 403-on-list (households) + `isSiteAdmin` in `FeedbackListDto`; no context endpoint.
- **R-C5 — Site-admin email plumbing + 403 gate.** `UserContextResolver` drops the email; `IsSiteAdmin(email)` needs it. The site-admin handlers read `principal.FindFirst(ClaimTypes.Email)` directly AND call the resolver for household scope (don't re-thread email through the resolver). Per-handler 403 guard (3 routes don't justify a filter): `Results.Json(new { message = "Site admin access required." }, statusCode: 403)` — **non-empty body** (4xx quirk).
- **R-C6 — Feedback author 3-way mapping:** `User != null` → `AuthorName=DisplayName, AuthorDeleted=false`; `User==null && UserId!=null` → `AuthorName=null, AuthorDeleted=true` ("Deleted user"); `User==null && UserId==null` → `AuthorName=null, AuthorDeleted=false` (anonymous → render no author line). Spell all three out so the build doesn't show "Deleted user" for anonymous feedback.
- **R-C7 — Reject reason is OPTIONAL.** `RejectReasonDialog` labels it "(optional)", MaxLength 500, may return empty. `RejectRequest.Reason` is nullable/allow-empty — do NOT 400 on an empty reason.
- **R-C8 — Household-requests are site-admin GLOBAL, not household-scoped.** `HouseholdRequest` has no `HouseholdId`; the list reads ALL requests + ALL households cross-tenant, legitimately, gated by site-admin. The C test for these routes verifies the **403 gate**, not M1 household-scoping. (M1 still applies to feedback.) `MemberCount` = `Households.Include(Users)` → `Users.Count`.
- **R-C9 — Liveness:** keep it (only polling cluster); shared `liveness.ts` visible-only, 30s/15s, smart count-check on feedback. Visible-only is a tiny, strictly-better behavior change (no poll when tab hidden) — accepted. `loadSeq` guard still required.
- **R-C10 — Feedback type label:** the enum serializes camelCase on the wire (`featureRequest`); the island needs a label/icon/color map (today the chip shows the raw PascalCase enum name).

**Verdict: build-ready, but C is the one to code carefully** — R-C1 (IDOR), R-C2 (transaction), R-C3 (409 guard) are correctness/security-load-bearing and each needs its own test. Build last, after A and B verify.
