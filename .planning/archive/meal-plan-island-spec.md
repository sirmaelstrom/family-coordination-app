# Spec — Meal Plan → Svelte island (strangler)

**Quest:** Spine `773aac12-af21-4e9e-8788-f55c3170f10b` (campaign "Family Coordination App", horizon `now`).
**Decisions (operator, 2026-06-23):** spec-first **focused**; **parity-first** — migrate the current click-slot→picker surface as-is. Drag-to-assign is deferred to quest `1656427f` (provisional, `later`).
**Status:** SHIPPED — merged PR #51 (2026-06-23, commit 0340a84). Archived 2026-07-01 (doc-health reconciliation). Header below is the original spec.

---

## 1. Goal & scope

Replace the Blazor `MealPlan.razor` circuit-bound surface with a Svelte 5 island that talks to the server over plain HTTP/JSON — killing the tab-away / SignalR-circuit-drop failure mode (same motivation as the shopping-list + chores islands). **Behavior parity** with today's page; **no new UX**.

**In scope (parity):**
- Weekly view: calendar grid (desktop, md+) / day list (mobile, sm-) toggled by viewport.
- Week navigation: prev / next / jump-to-today. Monday-start weeks.
- Slot → recipe picker (3 modes: search existing recipe, custom-meal text, quick-create new recipe).
- Remove an entry (with a confirm).
- View a recipe's detail (read-only modal: image, times, servings, ingredients, instructions).
- View a custom-meal's notes (read-only).
- Cross-user freshness: a change by another member appears within ~20s while visible, immediately on tab refocus (mirror chores `liveness.ts`; **not** Blazor `DataNotifier`).

**Out of scope (this island):**
- Drag-to-assign (recipe→slot, move between slots) → deferred quest `1656427f`. The current page has **no** drag; adding it is new UX, not migration.
- Editing a meal entry in place (today you remove + re-add — keep that).
- "Planned by" avatars (today's UI shows none; `MealPlanEntry.UpdatedByUserId` exists if a future affordance wants it).

---

## 2. Pattern being mirrored (the template)

The **chores island** is the canonical, freshest end-to-end example. Mirror it; do not invent.
- `frontend/chores/` — Vite/Svelte-5 island (`main.ts` mount/heal/dark-mode, `App.svelte`, `lib/{types,api,state.svelte,liveness,dates}.ts`, `lib/components/*`, `styles/{app,tokens}.css`).
- `Endpoints/ChoresEndpoints.cs` — `/api/chores` group behind `.RequireAuthorization().DisableAntiforgery()`, every handler resolving household/user via `UserContextResolver` (M1, never client-supplied).
- `Services/Dtos/ChoreDtos.cs` ↔ `Fixtures/ChoreBoard/board.json` ↔ `ChoreBoardDtoContractTests` ↔ island `types.ts` — the **M9 four-way lockstep**.
- `Components/Pages/Chores.razor` — host: feature flag, `#…-root` data attributes, `/islands/…/index.js` import + global mounter, version cache-bust, `ensureIslandStylesheet`.
- `CopyChoresIsland` MSBuild target in the `.csproj`.

**What meal-plan drops vs chores (it is much simpler):** no claim state machine, no roster, no equity/recap/digest, no rooms, no xmin optimistic-concurrency dance. The closest chores analog is the **versionless subtasks path** (last-write-wins, reconcile-on-error).

---

## 3. Endpoint surface — `/api/meal-plan`

New file `Endpoints/MealPlanEndpoints.cs`, group `app.MapGroup("/api/meal-plan").RequireAuthorization().DisableAntiforgery()`. Every handler resolves the caller via `UserContextResolver.ResolveUserAsync(principal, dbFactory, ct)` → `HouseholdId`/`UserId` server-side (M1). All reads/writes filter by `HouseholdId`.

| Method & route | Body | Returns | Notes |
|---|---|---|---|
| `GET /board?weekStart=YYYY-MM-DD` | — | `MealPlanBoardDto` | Server **snaps `weekStart` to that week's Monday** (`IMealPlanService.GetWeekStartDate`). Missing/unparseable → current week (server today, household tz). **Read-only — does NOT create a plan**: no plan for the week ⇒ `mealPlanId:null, entries:[]`. |
| `POST /entries` | `AddEntryRequest` | `MealPlanEntryDto` (201) | Resolves household+user, delegates to `AddMealAsync` (GetOrCreates the week's plan). Both `recipeId`+`customMealName` set, or neither ⇒ **400**. |
| `DELETE /entries/{mealPlanId:int}/{entryId:int}` | — | 204 | `RemoveMealAsync`. Not found ⇒ 404 (may surface as empty 400 — see §9.1; island just refetches). |
| `GET /recipes?q=…` | — | `MealRecipeSummaryDto[]` | Picker autocomplete. `IRecipeService.GetRecipesAsync(householdId, q, ct)`. Empty `q` ⇒ all (matches current `MinCharacters=0`). |
| `POST /recipes` | `QuickCreateRecipeRequest` | `MealRecipeSummaryDto` (201) | "New Recipe" tab. `CreateRecipeAsync(new Recipe{ HouseholdId=resolved, Name, RecipeType, CreatedByUserId=user, CreatedAt=UtcNow })`. Island then POSTs an entry with the new id (two calls — mirrors today's flow). |
| `GET /recipes/{recipeId:int}` | — | `RecipeDetailDto` | Recipe-detail modal (read-only, lazy on view-click → keeps the board lean). `GetRecipeAsync`; not found ⇒ 404. |

**Request DTOs:**
```csharp
public sealed record AddEntryRequest(
    DateOnly Date, MealType MealType, int? RecipeId, string? CustomMealName, string? Notes);
public sealed record QuickCreateRecipeRequest(string Name, RecipeType RecipeType);
```
`Date` is the slot's calendar position the user clicked — legitimate client-supplied data (slot identity), not date-math. The server derives the week from it. The XOR (`RecipeId` ⊕ `CustomMealName`) is already enforced in `AddMealAsync`; map its `InvalidOperationException` → 400 (introduce a small typed `MealPlanValidationException`, or catch the existing exception at the endpoint).

---

## 4. Board DTO contract (M9 lockstep)

New file `Services/Dtos/MealPlanDtos.cs`. **All enums are real enum *types* on the DTO** → they serialize **camelCase** through the globally-registered `JsonStringEnumConverter(CamelCase)`. This deliberately avoids the chores `recurrenceMode`/`effortTier` PascalCase-plain-string trap (those were `.ToString()`'d). `DateOnly` serializes as `"YYYY-MM-DD"`.

```csharp
public sealed record MealPlanBoardDto(
    DateOnly WeekStartDate,                       // the Monday, echoed
    int? MealPlanId,                              // null when no plan yet
    IReadOnlyList<MealPlanEntryDto> Entries);

public sealed record MealPlanEntryDto(
    int MealPlanId,
    int EntryId,
    DateOnly Date,
    MealType MealType,                            // "breakfast"|"lunch"|"dinner"|"snack"
    MealRecipeSummaryDto? Recipe,                 // null for custom meals
    string? CustomMealName,
    string? Notes);

public sealed record MealRecipeSummaryDto(
    int RecipeId, string Name, string? ImagePath, RecipeType RecipeType);

public sealed record RecipeDetailDto(
    int RecipeId, string Name, string? ImagePath, RecipeType RecipeType,
    int? PrepTimeMinutes, int? CookTimeMinutes, int? Servings,
    string? Instructions, IReadOnlyList<RecipeIngredientDto> Ingredients);

public sealed record RecipeIngredientDto(
    decimal? Quantity, string? Unit, string Name, string? Notes, int SortOrder);
```

Projection lives in **one place** — a new `IMealPlanBoardService` (mirrors `IChoreBoardService`): `GetBoardAsync(householdId, weekStart, ct)` (read-only) and the per-entry projection reused by `POST /entries`' response, so the card view and the mutation response can't drift (M9 spirit). Recipe summary/detail projections live alongside.

**TS mirror** (`frontend/meal-plan/src/lib/types.ts`) — camelCase keys:
```ts
export type MealType = 'breakfast' | 'lunch' | 'dinner' | 'snack';
export type RecipeType =
  'main'|'side'|'appetizer'|'dessert'|'beverage'|'sauce'|'breakfast'|'snack'|'other';
export interface MealRecipeSummaryDto { recipeId:number; name:string; imagePath:string|null; recipeType:RecipeType }
export interface MealPlanEntryDto {
  mealPlanId:number; entryId:number; date:string;  // "YYYY-MM-DD" — NEVER new Date() it
  mealType:MealType; recipe:MealRecipeSummaryDto|null; customMealName:string|null; notes:string|null }
export interface MealPlanBoardDto { weekStartDate:string; mealPlanId:number|null; entries:MealPlanEntryDto[] }
export interface RecipeIngredientDto { quantity:number|null; unit:string|null; name:string; notes:string|null; sortOrder:number }
export interface RecipeDetailDto {
  recipeId:number; name:string; imagePath:string|null; recipeType:RecipeType;
  prepTimeMinutes:number|null; cookTimeMinutes:number|null; servings:number|null;
  instructions:string|null; ingredients:RecipeIngredientDto[] }
export interface ShellContext { householdId:number; userId:number; userName:string }
```

---

## 5. Concurrency — versionless / last-write-wins

Parity ops are **add** and **remove** (no in-place edit). So the wire contract carries **no `version`/xmin token** — mirror the chores *subtasks* path, not its xmin path. This answers the handoff's open question ("does a meal slot need xmin?") → **no, for parity.** (`MealPlanEntry.Version` exists on the entity but is unused here; the deferred drag-drop "move" quest can revisit if it needs it.)

Conflict story: two members editing the same week → last write wins; the 20s liveness poll + refocus-refetch reconciles. Removing an already-removed entry → 404/empty-400 → island refetches the week. This matches today's Blazor behavior (also last-write-wins via DataNotifier).

---

## 6. Island structure — `frontend/meal-plan/`

Copy the chores scaffold, strip what parity doesn't need (**no `svelte-dnd-action`**, no equity/recap/rooms/digest).

- **Build config:** `package.json` (name `meal-plan-island`, deps: just svelte + vite toolchain — drop `svelte-dnd-action`), `tsconfig.json`, `svelte.config.js`, `vite.config.ts` (port **5175**, `outDir:dist`, `input:src/main.ts`, `entryFileNames:index.js`, css→`index.css`, `/api` proxy → `:5000`), `index.html` (dev auto-mount).
- **`src/main.ts`** — copy chores verbatim, rename: root id `meal-plan-root`, global `window.MealPlanIsland = { mount, destroy }`, `ch-dark-mode` → `mp-dark-mode`. **Keep the `.NET 10` circuit-resume self-heal machinery as-is** — it's load-bearing reliability; do not reinvent.
- **`src/lib/types.ts`** — §4.
- **`src/lib/api.ts`** — `getBoard(weekStart)`, `addEntry(body)`, `removeEntry(mealPlanId, entryId)`, `searchRecipes(q)`, `quickCreateRecipe(body)`, `getRecipeDetail(id)`. Reuse the chores `ApiError` + `request<T>` (`credentials:'include'`); since versionless, no 409 branch needed — any non-2xx 4xx ⇒ client rejection ⇒ refetch.
- **`src/lib/state.svelte.ts`** — `MealPlanStore` (class-instance export — Svelte-5 rune rule):
  - `weekStart = $state<string>` (the Monday "YYYY-MM-DD"), `board = $state<MealPlanBoardDto|null>`, `loading`, `error`, `currentUserId`.
  - `entriesByDayMeal = $derived` — `Map<\`${date}|${mealType}\`, MealPlanEntryDto[]>` for slot lookup.
  - `changeWeek(deltaWeeks|targetMonday)` → recompute weekStart (§ dates), refetch board.
  - `addEntry(...)` — POST then merge returned entry into `board.entries` (await-then-insert; add is infrequent). On 4xx/network: reconcile + calm toast.
  - `removeEntry(mealPlanId, entryId)` — optimistic splice, DELETE, reconcile-on-error.
  - `setRefresh` / `reconcile` (re-GET the current week) shared by liveness + error reconcile (copy chores shape).
- **`src/lib/dates.ts`** — week helpers. **MN4/global-CORRECTION:** never `new Date('YYYY-MM-DD')`. Step weeks via `Date.UTC(y,m,d,12)` (noon UTC, DST-safe) → format the YYYY-MM-DD string + the "MMM d – MMM d, yyyy" label + weekday/day labels. The server re-snaps `weekStart` to Monday on every board GET, so client stepping can't corrupt the boundary.
- **`src/lib/liveness.ts`** — copy chores verbatim (20s visible-only poll + `visibilitychange` immediate refetch).
- **`src/App.svelte`** — read `ShellContext` from root data-attrs; init week = current; load board; render `WeekNav` + (`CalendarGrid` md+ / `DayList` sm-) via a CSS/match-media split; mount `Toasts`; wire liveness to `reconcile`.
- **`src/lib/components/`:**
  - `WeekNav.svelte` — prev/next/jump-to-today + week-range label + "This week" chip.
  - `CalendarGrid.svelte` — 7-col × 3-row (Breakfast/Lunch/Dinner) grid (desktop). *(Snack: today's grid renders only B/L/D rows even though the enum has Snack; preserve that — Snack reachable only if data exists. Match current `WeeklyCalendarView`.)*
  - `DayList.svelte` — per-day expandable sections (mobile), B/L/D rows.
  - `MealSlot.svelte` — empty (＋) or entries (recipe card w/ image+name+type chip, or custom-meal w/ notes) + remove (×) + "add side/dessert".
  - `RecipePickerSheet.svelte` — replaces `RecipePickerDialog` MudDialog. 3 modes: **search** (debounced `searchRecipes`, image+name rows), **custom meal** (name + notes), **new recipe** (name + type select → `quickCreateRecipe` → add entry). Notes field shared across modes.
  - `RecipeDetailSheet.svelte` — lazy `getRecipeDetail(id)` → image, time/servings chips, ingredients (format `qty unit name (notes)`), instructions (sanitized markdown → reuse a tiny client renderer or render server-sanitized HTML; today it's `MarkdownHelper.ToSafeHtml` server-side — **decide:** return pre-sanitized `instructionsHtml` from the detail endpoint to avoid shipping a markdown lib client-side). → **Resolve in WP-1:** add `InstructionsHtml` (server-sanitized) to `RecipeDetailDto`, drop raw `Instructions`, render via `{@html}`.
  - `CustomMealSheet` — fold into `RecipeDetailSheet` (read-only notes view) or a tiny dedicated view.
  - `Toasts.svelte` + `toasts.svelte.ts` — copy chores.
- **`src/styles/{app,tokens}.css`** — copy chores (MudBlazor token bridge + dark-mode); rename the `mp-dark-mode` scope.

---

## 7. Host page + build wiring

- **`Components/Pages/MealPlan.razor`** — mirror `Chores.razor`. Feature flag **`MEAL_PLAN_USE_ISLAND`** (config-or-env, same helper shape). **Flag ON → island** (`#meal-plan-root` w/ `data-household-id`/`data-user-id`/`data-user-name`, `/islands/meal-plan/index.js` import + `MealPlanIsland.mount`, version cache-bust, `ensureIslandStylesheet`). **Flag OFF → the existing Blazor page body** (keep `IMealPlanService`/dialogs intact as the zero-regression fallback during migration — flip the flag to switch; delete the Blazor path in a later cleanup). Mirror `IAsyncDisposable` teardown + `JSDisconnectedException` guard.
- **`CopyMealPlanIsland`** MSBuild target in `FamilyCoordinationApp.csproj` — copy of `CopyChoresIsland`, `frontend/meal-plan/dist/**` → `wwwroot/islands/meal-plan/`, `Condition="Exists(... frontend/meal-plan/dist)"`, `BeforeTargets="AssignTargetPaths"`.
- **Dockerfile** — add a `mealplan-node-build` stage (copy of `chores-node-build`): `npm ci && npm run build` in `frontend/meal-plan/`; the .NET stage `COPY --from=mealplan-node-build … frontend/meal-plan/dist`. *(Verify exact stage names in the Dockerfile during WP-5.)*
- **`Program.cs`** — register `IMealPlanBoardService`; call `app.MapMealPlanEndpoints()`. (`IMealPlanService`, `IRecipeService` already registered.) Confirm the global JSON options register `JsonStringEnumConverter(CamelCase)` (the contract test depends on it).
- **`ensureIslandStylesheet` / `import` JS helpers** — reuse the existing ones `Chores.razor` calls (locate in `wwwroot` during WP-5; do not duplicate).

---

## 8. Contract & integration tests (M9)

- **`MealPlanBoardDtoContractTests`** (mirror `ChoreBoardDtoContractTests`): build a representative `MealPlanBoardDto` (a week with a recipe entry w/ image, a recipe entry w/o image, a custom-meal entry w/ notes, entries across multiple days + meal types, a non-null `mealPlanId`); serialize with the **same global `JsonSerializerOptions`** (`JsonSerializerDefaults.Web` + `JsonStringEnumConverter(CamelCase)`, `WriteIndented`); assert byte-equality against checked-in `Fixtures/MealPlanBoard/board.json`. The island `types.ts` mirrors that fixture → any shape/casing drift breaks the test. (Optional second fixture for `RecipeDetailDto`; lower priority — read-only, low-risk.)
- **Endpoint integration (real Postgres, mirror chores endpoint tests):** board read for a week (empty + populated); add-entry round-trip (recipe + custom); add-entry XOR validation → 400; remove → 204 then gone; remove-missing → 404; recipe search; quick-create then board reflects it; **cross-household isolation** — a user in household A cannot read or mutate household B's plan/entries (M1).

---

## 9. Gotchas to carry (from shopping-list + chores)

1. **WP-08 4xx quirk** — the app's `UseStatusCodePagesWithReExecute` re-executes empty-body 404s as empty **400s**. Island treats **any 4xx** as a non-retryable rejection → refetch the week + calm toast. (No 409 here — versionless — so even simpler than chores.)
2. **Casing** — `MealType`/`RecipeType` are real enums on the DTO ⇒ **camelCase** via the global converter; `DateOnly` ⇒ `"YYYY-MM-DD"`. Document at the top of `types.ts`.
3. **Dates (MN4 / global CORRECTION)** — never `new Date('YYYY-MM-DD')`. Week-stepping uses `Date.UTC(...,12)` for **display only**; the server is authoritative on the week's Monday.
4. **Multi-tenant (M1)** — household/user always server-resolved; every query filters `HouseholdId`; cross-household integration test is mandatory.
5. **Island resilience** — reuse `main.ts`'s mount/heal/dark-mode self-heal verbatim (the `.NET 10` circuit-resume root-replacement fix). Load-bearing; don't reinvent.
6. **DbContextFactory** — services inject `IDbContextFactory<ApplicationDbContext>`, short-lived contexts (already true of `MealPlanService`/`RecipeService`).

---

## 10. Build sequence (work packages)

- **WP-1 — Backend.** `MealPlanDtos.cs`, `IMealPlanBoardService`+impl (board read + entry/summary/detail projections; resolve `InstructionsHtml` server-sanitized), `MealPlanEndpoints.cs` (6 routes), `Program.cs` wiring, `MealPlanValidationException`→400 mapping.
- **WP-2 — Tests.** `MealPlanBoardDtoContractTests` + `Fixtures/MealPlanBoard/board.json`; endpoint integration (real PG) incl. cross-household isolation.
- **WP-3 — Island scaffold.** `frontend/meal-plan/` config + `types.ts` + `api.ts` + `main.ts` + `styles/` + `App.svelte` skeleton. `svelte-check` 0/0, `vite build` green.
- **WP-4 — Island UI.** store + `WeekNav`/`CalendarGrid`/`DayList`/`MealSlot`/`RecipePickerSheet`/`RecipeDetailSheet`/`Toasts` + liveness. `svelte-check` 0/0, `vite build`.
- **WP-5 — Host + build wiring.** `MealPlan.razor` flag + mount (keep Blazor fallback), `CopyMealPlanIsland`, Dockerfile `mealplan-node-build` stage.
- **WP-6 — Verify.** Browser-verify on `:8080` (rebuild `familyapp:latest`, swap app container, DOM+psql — Chrome screenshots flake on this stack; memory `fca-local-browser-verify-recipe`); flip `MEAL_PLAN_USE_ISLAND=true` and exercise add/remove/picker/detail/week-nav.

---

## 11. Gates (chores precedent; worktree baseline = memory `fca-worktree-gate-baseline`)

- `dotnet build` clean.
- `dotnet test` full suite green incl. new contract + integration tests. *Baseline if working in a worktree:* 2 `ApiKeySecurityTests` fail (`.git` is a file) + 49 pre-existing `dotnet format` whitespace errors on master — "green" = **no NEW failures**.
- `svelte-check` 0 errors / 0 warnings in `frontend/meal-plan/`.
- `vite build` → `dist/index.js` + `dist/index.css`.
- Real-Postgres integration for the new endpoints passes.
- Browser-verify on `:8080` (island path) confirms parity.

---

## 12. Open items (non-blocking — resolve in-build)

- **Instructions rendering** — decided: server returns sanitized `InstructionsHtml` on `RecipeDetailDto` (reuse `MarkdownHelper.ToSafeHtml`), island `{@html}`s it → no client markdown lib. (WP-1.)
- **Snack row** — preserve current behavior (grid shows B/L/D; Snack only if data exists). (WP-4.)
- **Exact Dockerfile stage names** — confirm against the real Dockerfile. (WP-5.)
- **`ensureIslandStylesheet`/import helper location** — locate the existing `wwwroot` helpers Chores.razor uses. (WP-5.)
