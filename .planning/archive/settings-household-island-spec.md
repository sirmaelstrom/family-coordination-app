# Spec — Settings island A: Household settings (Categories + Manage Users) → Svelte island (strangler)

**Quest:** Spine `57544e66-65b7-4680-853b-74aed5b014f7` (campaign "Family Coordination App", horizon `now`). Cluster **A** of the 3-island settings strangler (`dbee5aea` decomposed 2026-06-25; operator chose cluster-into-islands).
**Decisions (this session, 2026-06-25):** spec-first **focused**; parity-first; **one island, two routes** (`/settings/categories` + `/settings/users`) via the recipes data-view-one-bundle pattern; one flag `SETTINGS_USE_ISLAND`, one PR.
**Status:** SHIPPED — merged PR #55 (2026-06-24, commit 14c3323). Archived 2026-07-01 (doc-health reconciliation). Header below is the original spec. (Note: shipped flag is `SETTINGS_HOUSEHOLD_USE_ISLAND`, not the `SETTINGS_USE_ISLAND` named below.)
**Template:** mirrors the shipped **recipes island** (`frontend/recipes/`, multi-route bundle + `svelte-dnd-action` drag-reorder) and the **dashboard island** (`frontend/dashboard/`, the freshest light scaffold). Backend mirrors `RecipesEndpoints.cs` (greenfield `/api` over existing/new services, M1).

---

## 1. Goal & scope

Replace the two Blazor circuit-bound household-settings pages with **one** Svelte 5 island over plain HTTP/JSON — same strangler motivation, but here the sequencing driver is **stripping MudBlazor deps before the shell keystone** (settings are low-traffic; circuit-drop ROI is low). **Behavior parity, no new UX.**

This cluster is **write-heavy** (unlike the read-only dashboard) but each surface is simple CRUD. Two routes, two domains, one bundle.

### In scope (parity)

**Categories (`/settings/categories`)** — `Categories.razor` (305L), over the existing `ICategoryService`:
- Add a category (name required, emoji, color). `Categories.razor:169-191`.
- Active list with **drag-reorder** (persists `SortOrder`). `:67-95,258-273`.
- Edit a category (the `CategoryEditDialog` — name/emoji/color). `:193-216`.
- Delete (soft) with an **"in use" confirm** when the category has ingredients (`HasIngredientsAsync` → "Delete Anyway"). `:218-242`.
- Deleted-categories section with **Restore**. `:98-123,244-256`.
- "Default" chip on default categories (read-only flag).

**Manage Users (`/settings/users`)** — `WhitelistAdmin.razor` (291L), currently **direct EF** (no service):
- List the household's members (email, name, whitelist status, "You" chip). `:141-160`.
- Add a member by email: re-enable if a disabled row exists; else create a new whitelisted `User`; **reject if the email belongs to another household** (cross-household collision). `:162-228`.
- Toggle whitelist (Enable/Disable) — hidden for self; Disable hidden when the member is the **last active** one. `:230-239,85-102`.
- Delete a member with a confirm — blocked for **self** and when it's the **last user**; FK `ON DELETE SET NULL` keeps their recipes/feedback. `:241-284`.

**Cross-cutting:**
- The `.NET 10` circuit-resume self-heal mount machinery, dark-mode bridge, MudBlazor token CSS — copied verbatim from recipes/dashboard, re-scoped (`set-`).
- Cross-user freshness is **out of scope** here (parity: neither page polls today — both are static load-on-init). No `liveness.ts` wiring. (A late add by another member is rare on settings; matches today's no-refresh behavior.)

### Out of scope
- Any new UX. The drag-reorder already exists (parity-keep).
- `CategoryEditDialog`/`RejectReasonDialog` MudDialogs → replaced by in-island dialogs (parity behavior).
- The other settings clusters (Connections = island B `b60c13cb`; HouseholdAdmin+FeedbackAdmin = island C `fd629f11`).
- Real-time/liveness (neither page has it today).

---

## 2. Pattern being mirrored (the template)

- **Multi-route one-bundle** (recipes D1): two Razor host pages (`Categories.razor`, `WhitelistAdmin.razor`) both import `/islands/settings/index.js` and mount `window.SettingsIsland`; the root carries `data-view="categories"|"users"`. `main.ts` reads `view` and mounts `CategoriesApp` or `UsersApp`. Cross-view nav is full-document (parity: separate Blazor pages today).
- **Drag-reorder** = `svelte-dnd-action` (mirror recipes' `IngredientList`); reorder updates `sortOrder` then persists via the sort-order endpoint (parity: today reorder persists immediately, `Categories.razor:258`).
- Copy verbatim + re-scope (`rc-`→`set-`, `recipes`→`settings`, `RecipesIsland`→`SettingsIsland`, `rc-dark-mode`→`set-dark-mode`): `main.ts` self-heal, `lib/api.ts` (`request<T>`/`ApiError`), `toasts*`, `styles/{tokens,app}.css`. **No `liveness.ts`** (§1).
- **`untrack()` one-time-load** + **`loadSeq` guard** (memories `svelte5-setup-effect-async-loader-loop`, `fca-island-async-race-guards`) — non-negotiable, even though this is write-heavy: each app loads once, mutations reload.
- **Write-race guard:** because this island mutates, each mutation `await`s then reloads the affected list; a `loadSeq` guard drops a stale reload that resolves after a newer one. (No autosave/draft — these are explicit button actions, not debounced edits.)

---

## 3. Endpoint surface

Two groups in a new `Endpoints/SettingsEndpoints.cs`, both `.RequireAuthorization().DisableAntiforgery()`, every handler resolving `(HouseholdId, UserId)` via `UserContextResolver` (M1, never client-supplied). Register `app.MapSettingsEndpoints()` in `Program.cs`.

### 3a. `/api/settings/categories` — over the existing `ICategoryService`

| # | Method & route | Body | Returns | Notes |
|---|---|---|---|---|
| 1 | `GET /` | — | `CategoryListDto` | `GetCategoriesAsync(hh, includeDeleted:true)` split into `active` (sorted) + `deleted` (parity `:162-167`). |
| 2 | `POST /` | `CategoryWriteRequest` | `CategoryDto` (201) | Name required ⇒ else 400. `CreateCategoryAsync(new Category{…, HouseholdId=hh, IsDefault=false})` (`:179-181`). |
| 3 | `PUT /{categoryId:int}` | `CategoryWriteRequest` | `CategoryDto` | `GetCategoryAsync` first → 404 (non-empty body) if missing/cross-household; else `UpdateCategoryAsync`. |
| 4 | `DELETE /{categoryId:int}` | — | 204 | Soft delete. `DeleteCategoryAsync` — household-scoped; missing ⇒ 404. |
| 5 | `POST /{categoryId:int}/restore` | — | 204 | `RestoreCategoryAsync`. |
| 6 | `PUT /sort-order` | `{ orderedIds: int[] }` | 204 | Map to `List<(CategoryId, SortOrder)>` by index → `UpdateSortOrderAsync`. |
| 7 | `GET /{categoryId:int}/in-use` | — | `{ inUse: bool }` | `HasIngredientsAsync(hh, name)` for the delete confirm (look up the category's name server-side from the id, M1). |

### 3b. `/api/settings/members` — greenfield (today's logic is in the Razor; lift it to a thin `IHouseholdMemberService` for testability)

| # | Method & route | Body | Returns | Notes |
|---|---|---|---|---|
| 8 | `GET /` | — | `MemberListDto` | Household users ordered by email + the **caller's** `currentUserId` (so the island can render "You" + gate self-actions without a second call). |
| 9 | `POST /` | `{ email }` | `MemberActionDto` | Add/re-enable (parity `:162-228`): normalize lower; **another household ⇒ 409** (`{ message }`); existing-disabled ⇒ re-enable; existing-active ⇒ 409/no-op message; else create whitelisted `User`. |
| 10 | `PUT /{userId:int}` | `{ isWhitelisted: bool }` | `MemberDto` | Toggle. **Reject toggling self ⇒ 400**; reject disabling the **last active** member ⇒ 409 (parity `:85,250-252`). |
| 11 | `DELETE /{userId:int}` | — | 204 | Delete. **Reject self ⇒ 400**; reject last user ⇒ 409 (parity `:244-254`). FK SET NULL keeps recipes/feedback. |

> The member rules (self-guard, last-user-guard, cross-household collision) are the **whole risk surface** here — they live in `IHouseholdMemberService` so they're unit-/integration-testable (the Razor had no test harness). Mirrors the dashboard `ChoreHomeStats`-stays-server-side principle.

**Request DTOs** (`SettingsEndpoints.cs`):
```csharp
public sealed record CategoryWriteRequest(string Name, string? IconEmoji, string Color);
public sealed record SortOrderRequest(IReadOnlyList<int> OrderedIds);
public sealed record AddMemberRequest(string Email);
public sealed record SetWhitelistRequest(bool IsWhitelisted);
```

---

## 4. DTO contract (M9 lockstep)

New `Services/Dtos/SettingsDtos.cs` + projections (a thin `ISettingsProjectionService` or static mappers). No enums here ⇒ plain camelCase; no dates on the wire except `Category.DeletedAt` (render-only) — emit as ISO string and format client-side at noon-UTC (MN4).

```csharp
public sealed record CategoryListDto(
    IReadOnlyList<CategoryDto> Active, IReadOnlyList<CategoryDto> Deleted);
public sealed record CategoryDto(
    int CategoryId, string Name, string? IconEmoji, string Color,
    bool IsDefault, int SortOrder, string? DeletedAt);   // DeletedAt: "YYYY-MM-DD" or null

public sealed record MemberListDto(
    int CurrentUserId, IReadOnlyList<MemberDto> Members);
public sealed record MemberDto(
    int UserId, string Email, string? DisplayName, bool IsWhitelisted);
public sealed record MemberActionDto(MemberDto Member, string Outcome); // "created"|"reenabled" (for the toast)
```

**TS mirror** (`frontend/settings/src/lib/types.ts`) — camelCase, doc-commented; `IslandView = 'categories' | 'users'`; `ShellContext { householdId, userId, userName, view }`.

**Contract tests** (mirror `DashboardDtoContractTests`): `CategoryListDtoContractTests` + `Fixtures/Settings/categories.json`; `MemberListDtoContractTests` + `Fixtures/Settings/members.json` — byte-equality, camelCase, the island `types.ts` mirrors the fixtures.

---

## 5. Decisions

- **D1 — One island, two routes (recipes pattern).** Both pages cluster (operator's choice); one bundle, view by `data-view`. Cross-view nav is full-document. *Alt:* two separate islands — rejected: they share scaffold + flag + a settings nav, and clustering was the chosen granularity.
- **D2 — Lift the member logic into `IHouseholdMemberService`.** Today it's direct EF in the Razor (the one M1/safety risk surface). A thin service makes the self-guard / last-user / cross-household rules testable and keeps the endpoint thin (parity-correctness, mirrors dashboard's reuse-the-tested-reducer principle). Categories already has `ICategoryService` — reuse as-is.
- **D3 — Categories drag-reorder persists immediately** (parity `:258-273`): reorder → `PUT /sort-order { orderedIds }`; on failure, reload to restore order + toast. `svelte-dnd-action` (mirror recipes).
- **D4 — No liveness** (parity: neither page polls). Mutations reload the affected list; `loadSeq` guards stale reloads. Drop `liveness.ts` from the scaffold.
- **D5 — Delete-in-use confirm stays a round-trip** (`GET /{id}/in-use` before the soft delete, parity `:221`), shown as an in-island confirm dialog ("Delete Anyway").

---

## 6. Island structure — `frontend/settings/` (prefix `set-`, port 5178, roots `settings-categories-root`/`settings-users-root`)

Copy the recipes scaffold (it already has the two-root `data-view` branch + `svelte-dnd-action`); strip recipes-specific stores/components; **drop `liveness.ts`/`quantity.ts`/`dates`-week**. Add a tiny `dates.ts` (format `DeletedAt` only).

- **Build config:** `package.json` (name `settings-island`, deps incl. `svelte-dnd-action`), `vite.config.ts` (port 5178), tsconfig/svelte.config, `index.html` (dev auto-mount both roots).
- **`main.ts`** — recipes' two-root `data-view` branch, re-scoped; mounts `CategoriesApp` (`view==='categories'`) or `UsersApp` (`'users'`). Self-heal + dark observer unchanged.
- **`lib/types.ts`** §4; **`lib/api.ts`** — `request<T>`/`ApiError` + the 11 functions; **`lib/toasts*`** copied.
- **`lib/categoriesStore.svelte.ts`** — `active`/`deleted = $state`, `loading`/`error`, `#seq`; `load()`, `add()`, `update()`, `remove(id)` (in-use check → confirm → delete), `restore(id)`, `reorder(orderedIds)` (optimistic local + persist + reload-on-error).
- **`lib/membersStore.svelte.ts`** — `members`/`currentUserId = $state`, `loading`/`error`, `#seq`; `load()`, `add(email)` (handle 409 outcomes → toast), `toggle(id)`, `remove(id)`. Self/last-user gating mirrors the server (also enforced client-side for button visibility, parity `:83-113`).
- **`CategoriesApp.svelte`** / **`UsersApp.svelte`** — each: read `ShellContext`, the **`untrack()` one-time load**, render the page. Categories: add form, `svelte-dnd-action` active list (`CategoryRow`), deleted list, `CategoryEditDialog`, `ConfirmDialog` (in-use + delete). Users: add-by-email form, members table (`MemberRow`), enable/disable/delete with confirm.
- **`lib/components/`** (re-scope `set-`): `CategoryRow`, `CategoryEditDialog`, `MemberRow`, `ConfirmDialog` (copy recipes'), `Toasts`.
- **`styles/{tokens,app}.css`** copied + re-scoped; port the small inline styles.

---

## 7. Host pages + build wiring

- **`Categories.razor`** + **`WhitelistAdmin.razor`** → thin island hosts, flag **`SETTINGS_USE_ISLAND`** (mirror `Home.razor`/`MealPlan.razor` `IsIslandEnabled`). ON → `<div id="settings-{categories|users}-root" data-… data-view="…">` + mount; OFF → `<CategoriesBlazor/>` / `<WhitelistAdminBlazor/>` verbatim fallbacks (extract the current bodies). One flag toggles both.
- **`CopySettingsIsland`** MSBuild target (copy of `CopyDashboardIsland`).
- **Dockerfile** — `settings-node-build` stage + `COPY --from` (next to dashboard).
- **`docker-build.sh`** — `build_island "./frontend/settings" "settings"`.
- **`Program.cs`** — `app.MapSettingsEndpoints()`; register `IHouseholdMemberService` (+ projection if used). `ICategoryService` already registered.
- **Flag** — `SETTINGS_USE_ISLAND` in `docker-compose.yml` (`:-false`) + local `.env` (=true). Ship OFF; flip after verify.

---

## 8. Tests (M9)

- **Contract:** `CategoryListDtoContractTests` + `MemberListDtoContractTests` + fixtures (§4).
- **`HouseholdMemberService` unit/integration:** add-new, add-reenable, add-cross-household ⇒ 409, toggle-self ⇒ 400, disable-last-active ⇒ 409, delete-self ⇒ 400, delete-last ⇒ 409, delete keeps recipes (FK SET NULL).
- **Endpoint integration (real PG, mirror `DashboardEndpointTests`):** categories CRUD + reorder + restore + in-use; members list/add/toggle/delete with all guards; **cross-household M1** (A cannot read/mutate B's categories or members); 401 gate.

---

## 9. Build sequence (work packages)

- **WP-1 — Backend.** `SettingsDtos`, `IHouseholdMemberService`+impl, `SettingsEndpoints` (categories 7 + members 4), `Program.cs` wiring.
- **WP-2 — Backend tests.** Contract + fixtures; member-service guards; endpoint integration (real PG) incl. M1.
- **WP-3 — Island scaffold.** Copy recipes; re-scope `set-`; drop liveness/quantity; `types.ts`/`api.ts`/`main.ts`/styles; two App skeletons. `svelte-check` 0/0, `vite build`.
- **WP-4 — Categories view.** `categoriesStore`, `CategoriesApp` (untrack load), `CategoryRow` (dnd), `CategoryEditDialog`, in-use/delete confirms, restore.
- **WP-5 — Users view.** `membersStore`, `UsersApp` (untrack load), `MemberRow`, add/toggle/delete + guards.
- **WP-6 — Host + wiring.** Two thin hosts + verbatim Blazor fallbacks, `CopySettingsIsland`, Dockerfile stage, `docker-build.sh`, `SETTINGS_USE_ISLAND`.
- **WP-7 — Verify.** `:8080` (rebuild, swap, DOM/psql); flip flag; exercise both views (add/edit/delete/restore/reorder/toggle/guards); the **loop-check** gate (~0 bg GETs/sec — both apps load-once, no liveness).

## 10. Gates
`dotnet build` clean · full suite green incl. new tests · `svelte-check` 0/0 · `vite build` · real-PG integration (incl. M1) · loop-check · `:8080` parity verify.

## 11. Open items — RESOLVED (plan-review session, 2026-06-24)
- **Member rules → `IHouseholdMemberService`.** RESOLVED: yes, lift to the service (testability; also fixes the long-lived `_context` anti-pattern below). All three guards (self, last-active, last-user) and the cross-household collision check move server-side.
- **Emoji field.** RESOLVED: keep a plain freeform text field (parity); no emoji picker.
- **Flag.** RESOLVED: one flag **`SETTINGS_HOUSEHOLD_USE_ISLAND`** for cluster A's two routes (renamed from `SETTINGS_USE_ISLAND` — see X1). Per-cluster, not one shared settings flag.

---

## 12. Review resolutions (plan-review session, 2026-06-24)

*Decisions below are locked; fold into the build. Cross-cutting items (X*) are decided identically across A/B/C — see the cross-plan review memo `D:/Development/data/outputs/reviews/settings-strangler-plan-review/`.*

**Cross-cutting (apply to A):**
- **X1 — Flag = `SETTINGS_HOUSEHOLD_USE_ISLAND`** (per-cluster). Rename every `SETTINGS_USE_ISLAND` reference in this spec accordingly. Rationale: clusters ship in separate PRs and flip independently after their own verify; a single shared flag couldn't flip until all three islands exist.
- **X2 — Endpoints file:** `Endpoints/SettingsEndpoints.cs` (categories + members), its own `MapSettingsEndpoints()` + `Program.cs` registration.
- **X5 — Dates:** emit `Category.DeletedAt` as a **full ISO-8601 instant (UTC, `Z`)**, NOT `"YYYY-MM-DD"`. Format client-side with `new Date(iso).toLocaleDateString()`. (Noon-UTC normalization is only for bare date-only wire values; `DeletedAt` is a real timestamp. Update §4's DTO comment from `"YYYY-MM-DD" or null` to "ISO-8601 instant or null".)

**Cluster-A hardening (verified against the Razor source):**
- **R-A1 — Add-member outcome severity.** Today `AddUser` yields three severities: another-household → **Error** (abort); already-active-here → **Warning** (then clears+reloads); disabled→re-enable / new→create → **Success**. Endpoint #9 must therefore: return **409** ONLY for the cross-household collision; return **200** with `MemberActionDto.Outcome ∈ {created, reenabled, alreadyActive}` for the benign cases. The island toasts `alreadyActive` as a **warning** (not an error). Do NOT map "already active" to 409 (would flip warning→error, a parity regression).
- **R-A2 — Guards become server-enforced (deliberate, beyond strict parity).** Today the self / last-active guards are **UI-only** (buttons simply not rendered; `ToggleUser` does no re-check). The service WILL enforce toggle-self → 400, disable-last-active → 409, delete-self → 400, delete-last-user → 409. This rejects crafted requests that would succeed today — the right call for a testable service, but flagged as a deliberate behavior addition, not silent. Note the two distinct guards: **toggle** = last-*active* (`Count(IsWhitelisted) > 1`); **delete** = last-*user* (`Count > 1`, total incl. disabled).
- **R-A3 — Enumerate User-creation fields** in the service (a build will drop them otherwise): `DisplayName = email.Split('@')[0]`, `GoogleId = null`, `IsWhitelisted = true`, `CreatedAt = DateTime.UtcNow`, `HouseholdId = caller`.
- **R-A4 — Intentional cross-household read.** The collision check (`Users.Where(Email == x && HouseholdId != mine)`) is the one intentional cross-tenant query; it leaks no data (returns only a 409). The M1 test asserts the 409 AND that no row data crosses.
- **R-A5 — Route ordering (learned in #53):** register literal routes BEFORE parameterized ones in the group — `PUT /sort-order` before `PUT /{categoryId:int}`; `POST /{id}/restore` and `GET /{id}/in-use` are literal-suffixed so fine, but keep all literals ahead of bare `PUT/DELETE /{categoryId:int}`. Otherwise the parameterized route shadows the literal → 404 → re-executed to 405.
- **R-A6 — Side benefit:** lifting to `IHouseholdMemberService` with short-lived factory contexts retires today's long-lived `_context` field in `WhitelistAdmin.razor` (a Blazor-Server anti-pattern).
- **R-A7 — 4xx bodies:** every 400/404/409 carries a non-empty `{ message }` body (empty-body quirk). `MudColorPicker` → an `<input type="color">` (hex parity, e.g. default `#808080`).

**Verdict: build-ready.** A is the simplest cluster and the `now` quest — proceed to WP-1 after the operator clears the gate.
