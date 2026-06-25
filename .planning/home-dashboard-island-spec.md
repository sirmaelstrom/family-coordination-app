# Spec — Home/dashboard → Svelte island (strangler)

**Quest:** Spine `62fe9326-af6f-43c3-bc0c-ff3031b9d354` (campaign "Family Coordination App", horizon `next` → pull to `now` on build).
**Decisions (this session, 2026-06-24):** spec-first **focused**; **parity-first** — migrate today's read-only dashboard as-is, no new UX. **One aggregate endpoint** (`GET /api/dashboard`), **Landing.razor stays Blazor**, **read-only island** (no writes). Rationale in §5.
**Status:** spec (awaiting operator review gate).
**Template:** mirrors the shipped **meal-plan island** (`frontend/meal-plan/`) + its focused spec (`.planning/meal-plan-island-spec.md`) — the closest precedent for a *light* island. Recipes (`frontend/recipes/`, `.planning/recipes-island-spec.md`) is the secondary reference for the freshest scaffold conventions.

---

## 1. Goal & scope

Replace the Blazor `Components/Pages/Home.razor` circuit-bound dashboard (`@page "/dashboard"`, 402 lines) with a Svelte 5 island that talks to the server over plain HTTP/JSON — killing the tab-away / SignalR-circuit-drop failure mode (same motivation as shopping-list + chores + meal-plan + recipes). **Behavior parity** with today; **no new UX**.

This is the **lightest island so far**: read-only, **no mutations**. The whole page is quick-action cards that *aggregate* other domains (chores / shopping / meals) plus navigation links. The endpoint surface is a **single read-aggregate** (`GET /api/dashboard` → one `DashboardDto`), not a multi-route CRUD surface.

### In scope (parity)

- **Welcome banner** — "Welcome back, {greetingName}! 👋" + "{householdName} • {today, 'dddd, MMMM d'}". `Home.razor:17-24`.
- **Chores card** — heading + one-line status ("No chores yet" / "{N} need attention" / "All caught up"), and a body that is either the empty CTA, the "all caught up 🎉" line, or the stats rows (overdue / due-today / up-for-grabs with icons). Arrow → `/chores`; empty CTA → `/chores`. `Home.razor:27-103`.
- **Shopping List card** — heading + "{N} items remaining" / "All done!"; body is either the empty CTA ("Generate from meal plan" → `/shopping-list`) or a progress bar + "{checked} of {total} items checked". Arrow → `/shopping-list`. `Home.razor:106-155`.
- **Today's Meals card** — heading + "{today, 'MMM d'}"; body is either the empty CTA ("Plan something" → `/meal-plan`) or meal-type-grouped rows (icon + type label + each meal's display name). Arrow → `/meal-plan`. `Home.razor:158-206`.
- **Quick Actions** — 5 outlined link buttons: New Recipe (`/recipes/new`), Browse Recipes (`/recipes`), This Week (`/meal-plan`), Chores (`/chores`), Invite Family (`/settings/users`). `Home.razor:209-263`.
- **Cross-user freshness** — another member's chore/shopping/meal change appears within ~20s while visible + immediately on tab refocus (mirror meal-plan `liveness.ts`; **not** Blazor `DataNotifier`/`PollingService`). One `reconcile()` re-GETs `/api/dashboard`.
- **Dark-mode bridge, MudBlazor token CSS, the `.NET 10` circuit-resume self-heal mount machinery** — copied verbatim from meal-plan, re-scoped.

### Out of scope (this island)

- **`Components/Pages/Landing.razor` (`@page "/"`, 91 lines) — stays Blazor** (D2). It's the unauth marketing entry + redirect-to-`/dashboard` for authed users; near-zero circuit-drop exposure and the auth-state redirect is awkward to host in an island.
- **Any new UX.** All quick-actions remain plain navigations (today they're `Href` links). No new interactions.
- **Any writes.** The dashboard mutates nothing today (D3) — so no autosave/draft/optimistic/flush-before-delete patterns. The only async-race guard that applies is `loadSeq` (§8).
- The existing per-domain island endpoints (`/api/meal-plan`, `/api/chores`, `/api/shopping-list`) stay as-is — `/api/dashboard` reads through the **services**, it does not call those endpoints (D1).

---

## 2. Pattern being mirrored (the template)

The **meal-plan island** is the freshest *light* end-to-end example (`frontend/meal-plan/`). Mirror it; do not invent. What dashboard **drops** vs meal-plan (it is even simpler):

| Concern | meal-plan | dashboard |
|---|---|---|
| Endpoint routes | 6 (board + entries + recipes) | **1** (`GET /api/dashboard`) |
| Writes / mutations | add + remove entries | **none** (read-only) |
| Concurrency token | versionless | **N/A** (no writes) |
| Week / date math | `dates.ts` week-stepping | **no week math** — one display-label formatter only (§6) |
| `svelte-dnd-action` | none | none |
| State surface | week board + picker | **one `DashboardDto`** + liveness |

What dashboard **keeps identical** to meal-plan (copy verbatim, re-scope `mp-`→`db-`, `meal-plan`→`dashboard`, `MealPlanIsland`→`DashboardIsland`, `mp-dark-mode`→`db-dark-mode`, root id → `dashboard-root`):
- `src/main.ts` — mount/destroy + the **circuit-resume self-heal** (`isHealthy`/`MutationObserver`/`pageshow`) + dark-class observer. Load-bearing reliability — do **not** reinvent.
- `src/lib/liveness.ts` — 20s visible-only poll + `visibilitychange` immediate refetch.
- `src/lib/toasts.svelte.ts` + `lib/components/Toasts.svelte` — toast store + region (used only for the calm reconcile-error toast).
- `src/lib/api.ts` — `ApiError` + `request<T>` (`credentials:'include'`, any-4xx = client rejection).
- `src/styles/{tokens,app}.css` — MudBlazor token bridge + dark-mode scoping.
- The **`untrack()` one-time-load pattern** in `App.svelte` (§10) — **non-negotiable**.

---

## 3. Endpoint surface — `GET /api/dashboard`

New file `Endpoints/DashboardEndpoints.cs`, group:
```csharp
app.MapGroup("/api/dashboard").RequireAuthorization().DisableAntiforgery()
```
(`.DisableAntiforgery()` kept for consistency with the other island groups even though there are no writes.) **One route**, `GET /`. The handler resolves the caller via `UserContextResolver.ResolveUserAsync(principal, dbFactory, ct)` → `(HouseholdId, UserId)` **server-side (M1, never client-supplied)**; computes `greetingName` from the principal's claims (§5 D5); then delegates to a new `IDashboardService.GetDashboardAsync(householdId, userId, greetingName, ct)` which returns the whole `DashboardDto`. Register with `app.MapDashboardEndpoints();` in `Program.cs` (after `MapRecipesEndpoints()`). All services it needs are already DI-registered.

| # | Method & route | Body | Returns | Delegates to / notes |
|---|---|---|---|---|
| 1 | `GET /` | — | `DashboardDto` (200) | Own household only. Aggregates: household name + chore counts (`ChoreBoardService.GetBoardAsync` → `ChoreHomeStats.Compute`) + shopping summary (`ShoppingListService.GetActiveShoppingListsAsync` → checked/unchecked) + today's meals (focused EF query, today only). `greetingName` from claims chain. **Read-only** — never creates a meal plan/list/board row. `user is null` ⇒ 401. |

> No 404 path exists (the aggregate always returns a well-formed DTO with zero counts / empty lists for an empty household), so the empty-body-4xx quirk (§11.1) doesn't bite here — but the rule still holds for any future endpoint added to this group.

**Greeting helper** (private static in `DashboardEndpoints.cs`, mirrors `Home.razor:312-316`):
```csharp
private static string ResolveGreetingName(ClaimsPrincipal p) =>
    p.FindFirst(ClaimTypes.GivenName)?.Value
    ?? p.FindFirst(ClaimTypes.Name)?.Value?.Split(' ').FirstOrDefault()
    ?? p.FindFirst(ClaimTypes.Email)?.Value?.Split('@').FirstOrDefault()
    ?? "there";
```
The endpoint reads the principal (it has it) and passes the resolved string into the service, so the service stays claims-free and unit-testable with a literal name.

---

## 4. DTO contract (M9 lockstep)

New file `Services/Dtos/DashboardDtos.cs` + a new **single-projection** service `IDashboardService` (mirrors `IMealPlanBoardService`'s one-projection rule). **`MealType` is a real enum *type* on the DTO** ⇒ camelCase (`breakfast`/`lunch`/`dinner`/`snack`) via the globally-registered `JsonStringEnumConverter(CamelCase)` (`Program.cs:125-127`). `DateOnly Today` serializes as `"YYYY-MM-DD"`. `int` counts serialize as JSON numbers.

```csharp
public sealed record DashboardDto(
    string GreetingName,                          // claims chain (server) — never client-supplied
    string HouseholdName,
    DateOnly Today,                               // the SERVER "today" used for the meals query + the labels
                                                  // (echoed so the card header matches the data; client formats it)
    DashboardChoreSummaryDto Chores,
    DashboardShoppingSummaryDto Shopping,
    IReadOnlyList<DashboardMealDto> TodaysMeals); // ordered by MealType (parity: Home.razor OrderBy(MealType))

// Mirrors ChoreHomeStats.Result field-for-field (the four Home counts). "needs attention" = Overdue + DueToday
// is DISPLAY logic — computed client-side ($derived), NOT a wire field (keep the DTO = the raw reducer output).
public sealed record DashboardChoreSummaryDto(
    int ActiveTotal, int Overdue, int DueToday, int UpForGrabs);

// Progress % = Total>0 ? Checked*100/Total : 0 is DISPLAY logic — client-side. Total echoed for the "X of Y" label.
public sealed record DashboardShoppingSummaryDto(
    int Remaining, int Checked, int Total);       // Total = Remaining + Checked (across ALL active lists)

public sealed record DashboardMealDto(
    MealType MealType,                            // real enum ⇒ camelCase
    string DisplayName);                          // recipe name ?? customMealName ?? "Unnamed meal" (server applies)
```

**Why no nested full board/list DTOs** (D1): the dashboard needs *summary counts + today's meal names only*, not the chore board / shopping list / week board shapes. A lean purpose-built DTO keeps the payload tiny and the contract test trivial; reusing the heavy domain DTOs would couple the dashboard's contract to three other shipped contracts.

**TS mirror** (`frontend/dashboard/src/lib/types.ts`) — camelCase keys, doc-commented:
```ts
export type MealType = 'breakfast' | 'lunch' | 'dinner' | 'snack';
export interface DashboardChoreSummaryDto {
  activeTotal: number; overdue: number; dueToday: number; upForGrabs: number }
export interface DashboardShoppingSummaryDto {
  remaining: number; checked: number; total: number }
export interface DashboardMealDto { mealType: MealType; displayName: string }
export interface DashboardDto {
  greetingName: string; householdName: string;
  today: string;                                  // "YYYY-MM-DD" — format at NOON-UTC, never new Date(it)
  chores: DashboardChoreSummaryDto;
  shopping: DashboardShoppingSummaryDto;
  todaysMeals: DashboardMealDto[] }
export interface ShellContext { householdId: number; userId: number; userName: string }
```

**Projection** lives in **one place** — `IDashboardService.GetDashboardAsync(householdId, userId, greetingName, ct)` assembles the whole `DashboardDto`:
- **Household name** — `context.Users.Include(u => u.Household).FirstOrDefault(u => u.Id == userId)` → `Household?.Name ?? "Your Household"` (parity `Home.razor:329`). (One context; the chore/shopping/meal reads use their own services.)
- **Chores** — `ChoreBoardService.GetBoardAsync(householdId, userId, ct)` → `ChoreHomeStats.Compute(board.Chores)` → map `Result`→`DashboardChoreSummaryDto`. `ChoreHomeStats` is `internal static` in `FamilyCoordinationApp.Services` — the same assembly — so the service calls it directly (no re-implementation; the snooze guard on up-for-grabs stays server-side + already unit-tested). *(Parity note: `GetBoardAsync` builds the full board incl. rooms/rollups just to count — exactly what Home.razor does today. A leaner count-only query is harvested as a provisional perf quest, §14.)*
- **Shopping** — `ShoppingListService.GetActiveShoppingListsAsync(householdId, ct)` → flatten `.Items`; `Checked = count(IsChecked)`, `Remaining = count(!IsChecked)`, `Total = Checked + Remaining` (parity `Home.razor:357-369`, summing across **all** active lists).
- **Today's meals** — `today = DateOnly.FromDateTime(DateTime.Today)`; `weekStart = MealPlanService.GetWeekStartDate(today)`; focused EF query for that week's plan with `Entries.Where(Date == today)` + `.ThenInclude(Recipe)`; order by `MealType`; project each to `DashboardMealDto(e.MealType, GetMealDisplayName(e))`. `GetMealDisplayName` = `Recipe?.Name` (non-blank) ?? `CustomMealName` (non-blank) ?? `"Unnamed meal"` (parity `Home.razor:394-400`). Echo `Today = today`.

---

## 5. Decisions

**D1 — One aggregate `GET /api/dashboard`, NOT reuse of per-domain endpoints.** The dashboard reads chore counts + shopping summary + today's meals. The chore counts come from `ChoreHomeStats.Compute` — a server-side, unit-tested reducer whose up-for-grabs branch has a subtle `!IsSnoozed` guard (`ChoreHomeStats.cs:27`). Reusing the per-domain island endpoints would mean (a) **porting that reducer to TS** (the same parity-drift trap the recipes spec rejected for the NL ingredient parser — D2 there) and (b) **over-fetching** the full chore board + full meal-plan week for four counts + a handful of meal names. One aggregate keeps the reducer server-side, makes **one lean round-trip**, and yields one clean M9 contract. *Alt considered:* 3 client calls + client-side reduction — rejected on parity-drift + payload. *Tradeoff:* a new endpoint + service vs zero new backend — but the new surface is one route + one projection, smaller than reusing-and-reducing.

**D2 — `Landing.razor` (`/`) stays Blazor (out of scope).** It's the unauthenticated marketing page that redirects authed users to `/dashboard` (`Landing.razor:84-89`). It has effectively no interactive circuit state to drop, and hosting the `AuthenticationStateProvider` redirect inside an island is awkward cross-framework coupling for no reliability gain. Leave it. *(If the shell-keystone quest later de-Blazors the layout, Landing can ride along then.)*

**D3 — Read-only island: no writes ⇒ no autosave/optimistic/draft machinery.** Every interaction on Home.razor today is a navigation (`Href`), not a mutation. So the island has **no** POST/PUT/DELETE, no optimistic update, no draft autosave, no flush-in-flight-before-delete. The store is load + reconcile only. The single relevant async-race guard is `loadSeq` (§8) — a liveness/refocus refetch could land out of order against the initial load or another tick. Bake it in (cheap; the council flags it on every island even when marginal — memory `fca-island-async-race-guards`).

**D4 — Dates: server is authoritative on "today"; client formats labels only (no week math).** The server picks `today = DateTime.Today` (household-server tz, parity), runs the today-meals query against it, and **echoes `today`** in the DTO. The island formats two display labels from that one ISO string — the welcome banner (`"dddd, MMMM d"`) and the meals-card header (`"MMM d"`) — using a **noon-UTC** parse (`Date.UTC(y, m-1, d, 12)`), **never `new Date('YYYY-MM-DD')`** (global CORRECTION / MN4: bare string parse is UTC-midnight → wrong day in US tz). There is **no** week-stepping (unlike meal-plan), so `dates.ts` is a tiny two-formatter file. *Tradeoff:* around local midnight the server-"today" and the browser's wall-clock day can differ by one; today's Blazor uses server `DateTime.Today`, so echoing it is exact parity (and the card's data + header stay consistent because both derive from the one echoed date).

**D5 — Greeting resolved server-side from claims (parity chain).** `greetingName` = `GivenName` ?? first word of `Name` ?? email local-part ?? `"there"` (`Home.razor:312-316`). Resolved in the endpoint (it holds the `ClaimsPrincipal`) and passed into the service, so the service is claims-free + unit-testable. *Alt:* pass it as a host data-attr like `userName` — rejected: the host already only carries `userName` (full name); the fallback chain belongs server-side once, in the endpoint, not duplicated in Razor.

**D6 — Reuse `ChoreHomeStats` + the existing services verbatim; add no new query logic.** The aggregate service is pure composition over `IChoreBoardService` / `IShoppingListService` / `IMealPlanService` + a focused today-meals query that mirrors `Home.razor:338-355`. No new business logic, no recomputation — minimizes parity risk and rides the existing tests for those services.

---

## 6. Island structure — `frontend/dashboard/`

Copy the meal-plan scaffold; re-scope; **drop** `dates.ts` week math (replace with the tiny label formatter), drop the picker/board state. Renames: root id `dashboard-root`, global `window.DashboardIsland`, dark class `db-dark-mode`, CSS prefix `db-`, store `dashboardStore`.

- **Build config:** `package.json` (name `dashboard-island`; deps: svelte + vite toolchain only — no `svelte-dnd-action`), `tsconfig.json`, `svelte.config.js`, `vite.config.ts` (port **5177** — meal-plan=5175, recipes=5176; `outDir:dist`, `input:src/main.ts`, `entryFileNames:index.js`, css→`index.css`, `/api` proxy→`:5000`), `index.html` (dev auto-mount).
- **`src/main.ts`** — copy meal-plan verbatim; rename (root `dashboard-root`, global `DashboardIsland`, `db-dark-mode`). Keep the self-heal + dark observer **unchanged** (single root — no `data-view` branch needed, unlike recipes).
- **`src/lib/types.ts`** — §4.
- **`src/lib/api.ts`** — copy meal-plan's `request<T>`/`ApiError`; `BASE='/api/dashboard'`; **one** function `getDashboard(): Promise<DashboardDto>` (`GET /`). Any 4xx ⇒ `ApiError` → reconcile keeps the last good data + calm toast.
- **`src/lib/liveness.ts`** — copy verbatim (20s visible-only poll + `visibilitychange` immediate refetch).
- **`src/lib/toasts.svelte.ts` + `components/Toasts.svelte`** — copy + re-scope `db-`.
- **`src/lib/dates.ts`** — NEW (tiny): `formatLongDate(iso)` → `"dddd, MMMM d"` and `formatShortDate(iso)` → `"MMM d"`, both via `Date.UTC(y, m-1, d, 12)` then `toLocaleDateString`/manual month arrays (no `new Date('YYYY-MM-DD')`). No week math.
- **`src/lib/dashboardStore.svelte.ts`** — `DashboardStore` (class-instance export — Svelte-5 rune rule: export the instance, never a reassigned `$state`):
  - `data = $state<DashboardDto|null>(null)`, `loading = $state(true)`, `error = $state<string|null>(null)`, `currentUserId`, **private `loadSeq = 0`**.
  - `choreAttention = $derived(data ? data.chores.overdue + data.chores.dueToday : 0)`; `shoppingProgress = $derived(...)` (Total>0 ? checked/total*100 : 0); `mealsByType = $derived(...)` (group `todaysMeals` by `mealType` preserving order — parity `GroupBy(MealType).OrderBy(Key)`).
  - `init(ctx)` (set `currentUserId`), `setRefresh(fn)`, `load()` — `const seq = ++this.loadSeq; try { const d = await getDashboard(); if (seq === this.loadSeq) this.data = d; } catch { if (seq === this.loadSeq) … keep last good + set error } finally { if (seq === this.loadSeq) this.loading = false }`. `reconcile()` = `load()` (liveness + error path; the `loadSeq` guard makes a stale tick a no-op — memory `fca-island-async-race-guards`).
- **`src/App.svelte`** — read `ShellContext` from `dashboard-root` data-attrs; the **`untrack()` one-time load** (§10); render the welcome banner, the three cards (`ChoreCard`, `ShoppingCard`, `MealsCard`), `QuickActions`, `Toasts`; wire `liveness` → `reconcile`. While `loading && !data`, render a lightweight skeleton/spinner (parity: today the page renders after `OnInitializedAsync`).
- **`src/lib/components/`** (re-scope `db-`, Svelte-5 `$props`/`$derived`; plain HTML + token CSS mirroring the Mud cards):
  - `ChoreCard.svelte` — header (icon avatar, "Chores", status line via `choreAttention`/`activeTotal`), arrow→`/chores`; body branches: empty (CTA→`/chores`) / all-caught-up (🎉) / stats rows (overdue/due-today/up-for-grabs with the three icons). Mirror `Home.razor:27-103`.
  - `ShoppingCard.svelte` — header + "{remaining} items remaining"/"All done!", arrow→`/shopping-list`; body: empty (CTA→`/shopping-list`) / progress bar + "{checked} of {total} items checked". Mirror `Home.razor:106-155`.
  - `MealsCard.svelte` — header + short-date, arrow→`/meal-plan`; body: empty (CTA→`/meal-plan`) / `mealsByType` groups (type icon + label + each `displayName`). Meal-type icon map ports `GetMealTypeIcon` (`Home.razor:385-392`). Mirror `Home.razor:158-206`.
  - `QuickActions.svelte` — 5 anchor "buttons" (New Recipe / Browse Recipes / This Week / Chores / Invite Family) → plain `<a href>` full navigations. Mirror `Home.razor:209-263`.
  - `Toasts.svelte` — copy meal-plan's.
- **`src/styles/{tokens,app}.css`** — copy meal-plan; re-scope `db-`/`#dashboard-root` + `db-dark-mode`. Port the small inline `<style>` from `Home.razor:267-292` (`.welcome-section`, `.dashboard-card`, `.meal-group`, `.quick-action-btn`) into `app.css` with the `db-` scope. **No `dates.ts` week CSS.**

---

## 7. Host page + build wiring

- **`Components/Pages/Home.razor`** — convert to a thin island host, mirror `MealPlan.razor` exactly. Flag **`DASHBOARD_USE_ISLAND`** (same `IsIslandEnabled()` config-or-env helper, `MealPlan.razor:98-108`). **ON →** `<div id="dashboard-root" data-household-id data-user-id data-user-name>`, `<HeadContent>` `index.css?v=`, `OnAfterRenderAsync` `ensureIslandStylesheet` + `import('/islands/dashboard/index.js?v=')` + `DashboardIsland.mount("dashboard-root")`, `IslandVersion` cache-bust, `IAsyncDisposable` + `JSDisconnectedException` teardown calling `DashboardIsland.destroy`. **OFF →** `<HomeBlazor />`.
- **`Components/Pages/HomeBlazor.razor`** — NEW: the current `Home.razor` body extracted **verbatim** (the `@inject`s for `IMealPlanService`/`IShoppingListService`/`IChoreBoardService`/`IDbContextFactory`/`IHttpContextAccessor`, the cards, the `@code` block incl. `ChoreHomeStats` usage and the inline `<style>`). Zero-regression fallback — flip the flag to switch; delete in a later cleanup (§14). *(Note: `HomeBlazor` is NOT routable — no `@page` — it's a child component of the host. Move `@attribute [Authorize]` to the host `Home.razor`.)*
- **`CopyDashboardIsland`** MSBuild target in `FamilyCoordinationApp.csproj` — copy of `CopyMealPlanIsland`/`CopyRecipesIsland`: `frontend/dashboard/dist/**` → `wwwroot/islands/dashboard/`, `BeforeTargets="AssignTargetPaths"`, `Condition="Exists('…/frontend/dashboard/dist')"`, `SkipUnchangedFiles`.
- **Dockerfile** — edit the **multi-stage `Dockerfile`** (prod deploy + CI): add a `dashboard-node-build` stage (copy of `recipes-node-build`/`mealplan-node-build`): `node:20-alpine`, `npm ci`/`npm install`, `npm run build` in `frontend/dashboard/`; then `COPY --from=dashboard-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/dashboard/` in the .NET build stage. **Do NOT touch `Dockerfile.runtime-only`** (the local `docker-build.sh` path relies on `build_island` + the `CopyDashboardIsland` MSBuild target during `dotnet publish`).
- **`docker-build.sh`** — add `build_island "./frontend/dashboard" "dashboard"` (after the recipes line).
- **`Program.cs`** — `app.MapDashboardEndpoints();` (after `MapRecipesEndpoints()`); register `IDashboardService` (scoped). Already registered: `IChoreBoardService`, `IShoppingListService`, `IMealPlanService`, `JsonStringEnumConverter(CamelCase)` (`:125-127`).
- **Feature flag** — add `DASHBOARD_USE_ISLAND` to `docker-compose.yml` `app.environment` (`${DASHBOARD_USE_ISLAND:-false}`, next to the other island flags) and the repo's **`.env`** (local dev = `true`; match the other island flags — none are in `.env.example`). **Prod** flips it in the host's `~/familyapp/.env.local` after verify (deploy regenerates `.env` from it; `.claude/rules/deployment.md`) — ship **OFF** first, flip after verify.
- **`ensureIslandStylesheet` / `import` glue** — reuse the existing `window.ensureIslandStylesheet` in `Components/App.razor`; do not duplicate.

---

## 8. Concurrency / liveness

**No writes ⇒ no concurrency token, no conflict story.** The only ordering concern is overlapping reads: the initial load, a 20s liveness tick, and a `visibilitychange` refocus refetch can race. The **`loadSeq` monotonic guard** (§6 store) ensures only the newest response is applied — a slow earlier response that resolves after a newer one is dropped (memory `fca-island-async-race-guards`: "search/liveness/switch can apply a stale older response"). On a failed reconcile the store **keeps the last good `data`** and shows a calm toast (no blank-out). This matches today's Blazor (the page re-renders on `PollingService`/`DataNotifier` ticks; here liveness re-GETs the aggregate).

---

## 9. Contract & integration tests (M9)

- **`DashboardDtoContractTests`** (mirror `MealPlanBoardDtoContractTests`): build a representative `DashboardDto` — non-empty `greetingName`/`householdName`, a fixed `today`, chore counts with all four non-zero, a shopping summary mid-progress (checked + remaining), and `todaysMeals` spanning ≥2 meal types incl. a custom-meal name and an "Unnamed meal" — serialize with the **same global options** (`JsonSerializerDefaults.Web` + `JsonStringEnumConverter(CamelCase)`, `WriteIndented`); assert byte-equality against checked-in `Fixtures/Dashboard/dashboard.json`. The island `types.ts` mirrors the fixture → any shape/casing drift (esp. `mealType` camelCase + `today` as `"YYYY-MM-DD"`) breaks the test.
- **Greeting helper unit test** — `ResolveGreetingName` over: GivenName present; GivenName absent → first word of Name; Name absent → email local-part; all absent → `"there"`.
- **Endpoint integration (real Postgres, mirror chores/meal-plan endpoint tests):**
  - **Populated household** → chore counts equal `ChoreHomeStats.Compute(board.Chores)` (seed an overdue, a due-today, an unassigned/up-for-grabs incl. **a snoozed unclaimed chore that must NOT count as up-for-grabs**); shopping counts sum across **two** active lists; `todaysMeals` includes today's entries **but excludes tomorrow's** (date-filter guard) and reflects recipe-name vs custom-name vs unnamed.
  - **Empty household** → zero counts, `total:0`, `todaysMeals:[]`, `householdName` still set, well-formed 200 (no 404, no created rows).
  - **Read-only guarantee** → after the GET, assert no `MealPlan`/`ShoppingList`/`ChoreBoard` rows were created for a household that had none.
  - **Cross-household isolation (M1)** → a user in household A's `/api/dashboard` reflects **only** A's chores/shopping/meals, never B's (counts + meal names differ); the resolved household comes from the cookie, not any client input.

---

## 10. Loop safety (⚠ the #1 regression risk — bake in from day one)

Memory `svelte5-setup-effect-async-loader-loop`: a one-time setup `$effect` that synchronously reads `$state`/`$derived` it (transitively) writes subscribes to that state → infinite fetch→write→re-run loop (~30–46 req/s). It broke meal-plan visibly and chores silently (PR #52). `App.svelte`'s setup effect calls `store.load()` whose sync prefix reads/writes store state.

**Fix preemptively** — copy the meal-plan `App.svelte` pattern verbatim: wrap the **entire** setup-effect body in `untrack(() => { … })` so it has zero reactive deps and runs exactly once; liveness + refocus drive later refreshes:
```svelte
import { untrack } from 'svelte';
$effect(() => {
  untrack(() => {
    store.init(ctx);
    store.setRefresh(load);
    load();                                         // reads then writes store state — MUST be inside untrack
    liveness = startLiveness(() => store.reconcile());
  });
  return () => { liveness?.stop(); };
});
```

**MANDATORY GATE (WP-Verify):** open `/dashboard` on `:8080`, install a `fetch` counter in the console, confirm `/api/dashboard` does **~0 background GETs over several seconds** (initial load + one tick per 20s liveness) — **NOT tens/sec.** Re-check on prod after flipping the flag.

---

## 11. Gotchas to carry (from shopping-list + chores + meal-plan + recipes)

1. **Empty-body 4xx quirk** — `UseStatusCodePagesWithReExecute` re-executes empty-body 4xx through the GET-only Blazor `/not-found` page (memory `fca-empty-404-surfaces-as-405-on-delete`). This endpoint has no 4xx path (always 200 or 401), so it doesn't bite — but if a future route here can 404, return a **non-empty body** (`Results.NotFound(new { message })`).
2. **Casing** — `MealType` is a real enum on the DTO ⇒ **camelCase** (`"breakfast"`…); `DateOnly Today` ⇒ `"YYYY-MM-DD"`. Document at the top of `types.ts`.
3. **Dates (MN4 / global CORRECTION)** — never `new Date('YYYY-MM-DD')`. Format the echoed `today` for **display only** via `Date.UTC(...,12)`; the server is authoritative on which day is "today".
4. **Multi-tenant (M1)** — household/user always server-resolved via `UserContextResolver`; every read filters `HouseholdId`; cross-household integration test mandatory.
5. **Island resilience** — reuse `main.ts`'s mount/heal/dark-mode self-heal verbatim (the `.NET 10` circuit-resume root-replacement fix). Load-bearing; don't reinvent.
6. **DbContextFactory** — services inject `IDbContextFactory<ApplicationDbContext>`, short-lived contexts (already true of every service the aggregate touches).
7. **`ChoreHomeStats` is `internal`** — same assembly, so `DashboardService` calls `ChoreHomeStats.Compute` directly; do **not** copy its logic. If a future test project needs it, it's already reachable via `InternalsVisibleTo` (the existing `ChoreHomeStats` tests prove this).
8. **`HomeBlazor` is not routable** — the `@page "/dashboard"` + `@attribute [Authorize]` live on the host `Home.razor`; the extracted fallback is a plain child component (no `@page`). Keep its `@inject`s intact (zero-regression).

---

## 12. Build sequence (work packages)

- **WP-1 — Backend.** `Services/Dtos/DashboardDtos.cs` (§4), `IDashboardService`+impl (`GetDashboardAsync` — household name + `ChoreHomeStats` + shopping summary + focused today-meals query, §4 projection), `Endpoints/DashboardEndpoints.cs` (1 route + greeting helper), `Program.cs` wiring (`MapDashboardEndpoints`, register `IDashboardService`). Builds clean.
- **WP-2 — Backend tests.** `DashboardDtoContractTests` + `Fixtures/Dashboard/dashboard.json`; `ResolveGreetingName` unit test; endpoint integration (real PG) incl. snooze-guard chore counts, multi-list shopping sum, today-only meal filter, empty household, **read-only guarantee**, cross-household M1 (§9).
- **WP-3 — Island scaffold.** `frontend/dashboard/` config, `types.ts`, `api.ts` (1 fn), `main.ts` (single-root + self-heal), `liveness.ts`/`toasts*`/`styles` (copied + re-scoped from meal-plan), **`dates.ts` (tiny label formatters, §6)**, `App.svelte` skeleton. `svelte-check` 0/0, `vite build` green.
- **WP-4 — Island UI.** `DashboardStore` (loadSeq guard), `App` (the **`untrack` load**), `ChoreCard`/`ShoppingCard`/`MealsCard`/`QuickActions`/`Toasts`, welcome banner, liveness. `svelte-check` 0/0, `vite build`.
- **WP-5 — Host + build wiring.** `Home.razor` → thin host (flag + mount), `HomeBlazor.razor` (verbatim fallback extract), `CopyDashboardIsland`, Dockerfile `dashboard-node-build` stage + COPY, `docker-build.sh` entry, `DASHBOARD_USE_ISLAND` in compose + `.env`.
- **WP-6 — Verify.** Browser-verify on `:8080` (rebuild `familyapp:latest`, swap app container, DOM+psql — Chrome screenshots flake; memory `fca-local-browser-verify-recipe`); flip `DASHBOARD_USE_ISLAND=true`; exercise the three cards (populated + empty), the welcome banner date, quick-action navigations, and cross-user freshness (mutate a chore/shopping/meal as another member → appears ≤20s); **the LOOP-CHECK gate** (§10).

---

## 13. Gates (chores/meal-plan/recipes precedent; worktree baseline = memory `fca-worktree-gate-baseline`)

- `dotnet build` clean.
- `dotnet test` full suite green incl. new contract + integration tests. *Worktree baseline:* 2 `ApiKeySecurityTests` fail (`.git` is a file) + 49 pre-existing `dotnet format` whitespace errors on master — "green" = **no NEW failures**.
- `svelte-check` 0 errors / 0 warnings in `frontend/dashboard/`.
- `vite build` → `dist/index.js` + `dist/index.css`.
- Real-Postgres integration for the new endpoint passes (incl. cross-household M1 + read-only guarantee).
- **Loop-check** (§10): ~0 background GETs/sec.
- Browser-verify on `:8080` (flag ON) confirms parity.

---

## 14. Provisional quests to harvest (don't build now — author in the Spine)

- **Delete the Home Blazor fallback (`HomeBlazor.razor`).** Cleanup once the island is prod-stable (mirrors the meal-plan/recipes fallback-removal follow-ups).
- **Leaner chore-count query for the dashboard.** `GetBoardAsync` builds the full board (rooms/rollups/equity) just to count overdue/due-today/up-for-grabs; a dedicated count query (or a `GetChoreHomeStatsAsync`) would cut the dashboard's heaviest read. Perf-only; parity unaffected.
- **Fold `Landing.razor` into the strangler.** Migrate the `/` marketing/redirect page when the shell keystone de-Blazors the layout (D2).

---

## 15. Open items (non-blocking — resolve in-build)

- **Exact Dockerfile stage placement / COPY line** — confirm against the real Dockerfile at WP-5 (mirror the `recipes-node-build` stage just added in #53).
- **`HomeBlazor` extraction** — confirm the `@inject`s + `ChoreHomeStats` usage + inline `<style>` move cleanly and the host's `[Authorize]`/`@page` placement compiles (WP-5).
- **Skeleton/empty-state feel** — confirm the loading skeleton + empty-card CTAs read well on `:8080` (WP-6); they're parity but the island's first paint differs slightly from Blazor's server-prerender.
