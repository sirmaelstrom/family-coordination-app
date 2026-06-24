# Spec — Recipes + RecipeEdit → Svelte island (strangler)

**Quest:** Spine `10d187a0-3bce-43f8-bc09-ada75ac35b43` (campaign "Family Coordination App", horizon `now`).
**Decisions (operator, 2026-06-23):** **spec-first (deep)**; **combined** — one island bundle serving both `/recipes` (list) and `/recipes/new` + `/recipes/edit/{id}` (edit), one PR, one `RECIPES_USE_ISLAND` flag; **parity-first** — migrate today's surface as-is, harvest enhancements as provisional quests.
**Status:** spec (awaiting review gate).
**Template:** mirrors the shipped meal-plan island (`frontend/meal-plan/`) + its spec (`.planning/meal-plan-island-spec.md`). The chores island is the secondary reference (it has the `svelte-dnd-action` drag pattern recipes re-uses).

---

## 1. Goal & scope

Replace the two Blazor circuit-bound recipe surfaces (`Components/Pages/Recipes.razor`, 961 lines; `Components/Pages/RecipeEdit.razor`, 619 lines) with **one** Svelte 5 island that talks to the server over plain HTTP/JSON — killing the tab-away / SignalR-circuit-drop failure mode (same motivation as shopping-list + chores + meal-plan). **Behavior parity** with today; **no new UX**.

This is the **meatiest** island so far: two routes, a full edit form, natural-language ingredient parsing, drag-reorder, image upload + picker, URL import (scrape→JSON-LD→parse), bulk-paste, autosave drafts, connected-household sharing, and a live servings scaler. The greenfield `/api/recipes` surface (**20 routes** over **existing** server services) is the bulk of the design.

### In scope (parity)

**List surface (`/recipes`):**
- Recipe grid of cards (image / placeholder, name, type chip, "imported" cloud icon, author avatar, first-3-ingredient preview + "+N more"). `Recipes.razor:200-214`, `RecipeCard.razor`.
- Search by name / ingredients / type — **server-side**, debounced (300ms). `Recipes.razor:82-89`, `RecipeService.GetRecipesAsync`.
- Favorites: per-card heart toggle + a "Favorites" filter chip (client-side over the loaded set). `Recipes.razor:92-99,700-720`.
- Connected-household selector (chips): view a connected household's shared recipes **read-only** + "Copy to My Recipes". `Recipes.razor:55-79,591-648,764-786`.
- Detail **drawer** (slide-out, read-only): image, author, prep/cook chips, **live servings scaler** (rescales ingredient quantities client-side), description, ingredients, instructions (server-sanitized HTML), source link, and action buttons (Edit / Add to Meal Plan [stub] / Delete; or **Copy** for connected). `Recipes.razor:223-416`.
- Delete confirm dialog. `Recipes.razor:418-433,800-828`.
- Import-from-URL dialog (scrape pipeline, duplicate detection, "import anyway", error → manual-entry fallback, YouTube). `ImportRecipeDialog.razor`.
- "Add Recipe" → navigate to `/recipes/new`.

**Edit surface (`/recipes/new`, `/recipes/edit/{id}`):**
- Basic info: name (required), description, prep/cook time, servings, source URL, recipe type. `RecipeEdit.razor:73-131`.
- Image: file **upload** + **remove** + "Choose Existing" (household image **picker** grid). `RecipeEdit.razor:133-177`, `ImagePickerDialog.razor`.
- Ingredient **entry**: natural-language parse (Enter/Tab parses raw → editable qty/unit/name/category/notes fields), unit autocomplete, ingredient-name autocomplete (server suggestions), category select. `IngredientEntry.razor`.
- Ingredient **list**: **drag-reorder**, edit (remove + re-add), delete-with-undo. `IngredientList.razor`.
- **Bulk-paste**: multi-line textarea → parsed preview → import. `BulkPasteDialog.razor`.
- Instructions: markdown textarea. `RecipeEdit.razor:191-200`.
- **Autosave drafts** (2s debounce) + a "Draft saved" indicator + restore-on-load + nav-lock for unsaved changes. `RecipeEdit.razor:368-403,297-333`, `DraftService`.
- Save / Cancel / Delete.

**Cross-cutting:**
- Cross-user freshness on the **list**: another member's add/edit/delete appears within ~20s while visible + immediately on refocus (mirror meal-plan `liveness.ts`; **not** Blazor `DataNotifier`).
- Dark-mode bridge, MudBlazor token CSS, the `.NET 10` circuit-resume self-heal mount machinery — copied verbatim from meal-plan, re-scoped.

### Out of scope (this island)
- Any new UX. Drag-to-reorder already exists in `IngredientList` (parity-keep); we do **not** add drag elsewhere.
- "Add to Meal Plan" stays a stub info-toast (today it's "feature in development", `Recipes.razor:830-834`).
- The meal-plan island's `/api/meal-plan/recipes/*` picker endpoints stay as-is (see D11). We do not relocate or break them.
- Optimistic-concurrency (xmin) on recipe edit — parity is last-write-wins (D9). Harvested as a provisional quest.

---

## 2. Pattern being mirrored (the template)

The **meal-plan island** is the freshest, most complete end-to-end example (`frontend/meal-plan/`). Mirror it; do not invent. What recipes ADDS beyond meal-plan:

| Concern | meal-plan | recipes |
|---|---|---|
| Routes per island | 1 (`/meal-plan`) | **2** (`/recipes` list + `/recipes/new`+`/edit/{id}` edit) — one bundle, view chosen by root data-attr (D1) |
| Drag | none (dropped `svelte-dnd-action`) | **re-adds `svelte-dnd-action`** for ingredient reorder (mirror chores) |
| Forms | small picker sheet | **full edit form** + autosave drafts |
| NL parsing | none | **server `parse-ingredient`** endpoint (D2) |
| File upload | none | **multipart image upload** endpoint (D6) |
| External I/O | none | **URL import** pipeline endpoint (Polly, ≤60s) (D7) |
| Concurrency | versionless | versionless (parity; xmin available but unused — D9) |

What recipes KEEPS identical to meal-plan (copy verbatim, re-scope `mp-`→`rc-`, `meal-plan`→`recipes`, `MealPlanIsland`→`RecipesIsland`, `mp-dark-mode`→`rc-dark-mode`):
- `src/main.ts` — mount/destroy + the **circuit-resume self-heal** (`isHealthy`/`MutationObserver`/`pageshow`) + dark-class observer. Load-bearing reliability — do **not** reinvent.
- `src/lib/liveness.ts` — 20s visible-only poll + `visibilitychange` immediate refetch (list view only).
- `src/lib/toasts.svelte.ts` + `lib/components/Toasts.svelte` — toast store + region.
- `src/lib/api.ts` — `ApiError` + `request<T>` (`credentials:'include'`, any-4xx = client rejection).
- `src/styles/{tokens,app}.css` — MudBlazor token bridge + dark-mode scoping.
- The **`untrack()` one-time-load pattern** in `App.svelte` (§ Loop safety) — **non-negotiable**.

---

## 3. Endpoint surface — `/api/recipes`

New file `Endpoints/RecipesEndpoints.cs`, group:
```csharp
app.MapGroup("/api/recipes").RequireAuthorization().DisableAntiforgery()
```
Every handler resolves the caller via `UserContextResolver.ResolveUserAsync(principal, dbFactory, ct)` → `(HouseholdId, UserId)` **server-side (M1, never client-supplied)** (`Endpoints/UserContextResolver.cs:23-38`). Every read/write filters by `HouseholdId`. Register with `app.MapRecipesEndpoints();` in `Program.cs` (after `MapMealPlanEndpoints()`, `Program.cs:401`). All services it needs are already DI-registered (`Program.cs:83-95`).

| # | Method & route | Body | Returns | Delegates to / notes |
|---|---|---|---|---|
| 1 | `GET /` `?q=` | — | `RecipeListDto` | Own household. `RecipeService.GetRecipesAsync(hh, q)` + `GetFavoriteRecipeIdsAsync(uid, hh)` in one response. `q` server-filters name/desc/type/ingredient (parity). Empty `q` ⇒ all. |
| 2 | `GET /{recipeId:int}` | — | `RecipeFullDto` | Read drawer **and** edit-load (superset, D3). `GetRecipeAsync`; not found ⇒ 404 (non-empty body). |
| 3 | `POST /` | `RecipeWriteRequest` | `RecipeFullDto` (201) | Create. `CreateRecipeAsync(new Recipe{…, HouseholdId=hh, CreatedByUserId=uid})`. Name required ⇒ else 400. |
| 4 | `PUT /{recipeId:int}` | `RecipeWriteRequest` | `RecipeFullDto` (200) | Update. `UpdateRecipeAsync` replaces ingredients wholesale. **Two verified service traps:** (a) it does **NOT** assign `RecipeType` (`RecipeService.cs:94-104`) — a latent Blazor bug; this WP adds `existing.RecipeType = recipe.RecipeType;` (D12); (b) it returns the in-memory `existing` whose `Ingredients` nav is **stale** after RemoveRange/Add (`:106-132`) → the endpoint **re-fetches** `GetRecipeAsync(hh, recipeId, ct)` and projects THAT, never the `UpdateRecipeAsync` return. Missing ⇒ throws `InvalidOperationException` (`:89-92`) → **catch** → `Results.NotFound(new { message })` (M1: cross-household id = clean not-found). Sets `UpdatedByUserId=uid`. |
| 5 | `DELETE /{recipeId:int}` | — | 204 | Soft delete (`IsDeleted=true`). `DeleteRecipeAsync` **throws `InvalidOperationException` on missing** (`RecipeService.cs:142-145`) → **catch** → `Results.NotFound(new { message })` (non-empty body — mirror `MealPlanEndpoints.RemoveEntry`). |
| 6 | `POST /{recipeId:int}/favorite` | — | `{ isFavorite: bool }` | **`GetRecipeAsync(hh, recipeId, ct)` FIRST → 404 if null**, THEN `ToggleFavoriteAsync` + return `IsFavoriteAsync`. (A bare toggle inserts a `UserFavorite` directly → FK-violation 500 on a missing/cross-household id, `RecipeService.cs:206-230`; the pre-check yields a clean 404 + enforces M1.) |
| 7 | `GET /ingredient-suggestions` `?prefix=` | — | `string[]` | `GetIngredientSuggestionsAsync(hh, prefix)`. `<2` chars ⇒ `[]` (service guards). |
| 8 | `POST /parse-ingredient` | `{ text }` | `ParsedIngredientDto` | Server NL parse (D2). **`ParseIngredient` THROWS `ArgumentException` on empty** (`IngredientParser.cs:46`) — `string.IsNullOrWhiteSpace`-guard BEFORE calling and return 400; do **not** rely on a catch (an uncaught throw is a 500). Then `IIngredientParser.ParseIngredient(text)` + `ICategoryInferenceService.InferCategory(name)` → `suggestedCategory`. |
| 9 | `POST /parse-ingredients` | `{ lines: string[] }` | `ParsedIngredientDto[]` | Bulk-paste preview — parse each non-blank line server-side (one round-trip). |
| 10 | `GET /categories` | — | `CategoryDto[]` (`{ name }`) | `ICategoryService.GetCategoriesAsync(hh)` for the entry category select. |
| 11 | `POST /images` | `multipart/form-data` (`file`) | `{ imagePath }` (201) | `IImageService.SaveImageAsync(IFormFile, hh)` (existing `IFormFile` overload, `ImageService.cs:47`). 10 MB / jpg·png·gif·webp; else 400. |
| 12 | `GET /images` | — | `string[]` | `IImageService.ListImagesAsync(hh)` (returns `IEnumerable<string>` — `.ToArray()`) for the picker grid. |
| 13 | `POST /import` | `{ url, force?: bool }` | `RecipeImportResultDto` | `ImportFromUrlAsync` returns an **UNSAVED** `Recipe` (`RecipeImportResult.Recipe`, in-memory, `RecipeId==0`); on success the endpoint **must** `CreateRecipeAsync(result.Recipe!, ct)` and return the **assigned** `recipeId` (never `result.Recipe.RecipeId` before saving). Dup-check is **endpoint-level**: query own recipes for `SourceUrl == url` (exact string, mirror `ImportRecipeDialog.razor:140-147`; the global query filter already excludes deleted) → return `existingRecipeId`/`Name`; `force:true` bypasses **only** that endpoint check (`ImportFromUrlAsync` has **no** `force` param). ≤60s (Polly, D7). |
| 14 | `GET /connections` | — | `ConnectedHouseholdDto[]` | `IHouseholdConnectionService.GetConnectedHouseholdsAsync(hh)` → `{ householdId, householdName }`. |
| 15 | `GET /connected/{chId:int}` `?q=` | — | `RecipeListDto` (favoriteRecipeIds `[]`) | Connected household's shared recipes (read-only). **Validate connection first** (`AreHouseholdsConnectedAsync(chId, hh, ct)` — symmetric, `IHouseholdConnectionService.cs:13` ⇒ else 403); then `GetRecipesFromConnectedHouseholdAsync(hh, chId, q)` (already excludes `CreatedBy` → null author) → `ToListItem`. **Returns the same `RecipeListDto` shape as #1** (empty `favoriteRecipeIds`) so the one list store consumes both without a shape branch. |
| 16 | `GET /connected/{chId:int}/{recipeId:int}` | — | `RecipeFullDto` | Read-only detail of a connected recipe. Validate connection ⇒ else 403; then `recipeService.GetRecipeAsync(chId, recipeId, ct)` (connected id passed as `householdId` — the only fetch method; M1-safe since chId is connection-gated) → `ToFull(recipe, includeAuthor:false)` (strip author for privacy). |
| 17 | `POST /connected/{chId:int}/{recipeId:int}/copy` | — | `{ recipeId }` (201) | Validate connection ⇒ else 403; `CopyRecipeFromConnectedHouseholdAsync(chId, recipeId, hh, uid)`. |
| 18 | `GET /draft` `?recipeId=` | — | `RecipeDraftData` (200) or **204** | `DraftService.GetDraftAsync`. Omitted `recipeId` ⇒ new-recipe draft (**0 sentinel**, see below). **204 No-Content when no draft** (the island's `request<T>` treats 204 as null — `Results.Json(null)` writes an empty body that breaks `res.json()`, so don't use it). |
| 19 | `PUT /draft` | `SaveDraftRequest` (**FLAT**: `{ recipeId?, name, …draftFields, ingredients[] }`) | 204 | `DraftService.SaveDraftAsync`. **Body is flat**, not `{ recipeId, draft:{…} }`. |
| 20 | `DELETE /draft` `?recipeId=` | — | 204 | `DraftService.DeleteDraftAsync`. Idempotent. |

> **Build-time finding (drafts).** `RecipeDraft`'s composite PK is `(HouseholdId, UserId, RecipeId)` (`RecipeDraftConfiguration.cs:12`) with no FK on `RecipeId` — so a **null** `RecipeId` (new-recipe draft) throws `"primary key property 'RecipeId' is null"` (a latent bug — new-recipe autosave silently never worked in Blazor). The endpoints coalesce `recipeId ?? 0` (a safe sentinel; real recipe ids start at 1). The draft body is **flat** (`SaveDraftRequest` = recipeId + the draft fields + ingredients), mirroring the proven-binding `RecipeWriteRequest` shape, mapped to `DraftService.RecipeDraftData` in the handler.

**Request DTOs** (`RecipesEndpoints.cs`):
```csharp
public sealed record RecipeWriteRequest(
    string Name, string? Description, string? Instructions, string? SourceUrl,
    int? Servings, int? PrepTimeMinutes, int? CookTimeMinutes,
    RecipeType RecipeType, string? ImagePath,
    IReadOnlyList<RecipeIngredientWrite> Ingredients);
public sealed record RecipeIngredientWrite(
    string Name, decimal? Quantity, string? Unit, string Category,
    string? Notes, string? GroupName, int SortOrder);
public sealed record ParseIngredientRequest(string Text);
public sealed record ParseIngredientsRequest(IReadOnlyList<string> Lines);
public sealed record SaveDraftRequest(int? RecipeId, RecipeDraftData Draft);  // reuse DraftService's record — NOT a new payload type
public sealed record ImportRequest(string Url, bool Force = false);
```
`ImagePath` is a path the island already obtained from endpoint #11 (upload-then-reference, mirrors `RecipeEdit.razor:458-471`). `RecipeId` is NEVER in a write body — it's the route param (#4) or omitted (#3); the server assigns new ids via `GetNextRecipeIdAsync` (`RecipeService.CreateRecipeAsync`).

---

## 4. DTO contract (M9 lockstep)

New file `Services/Dtos/RecipeDtos.cs` + a new **single-projection** service `IRecipeProjectionService` (mirrors `IMealPlanBoardService`'s one-projection rule) with `ToListItem`, `ToFull(recipe, includeAuthor)` (the flag strips `createdByName`/`createdByPictureUrl` for connected-household reads — privacy), `ToParsed`. **All enums are real enum *types* on the DTO** ⇒ camelCase via the globally-registered `JsonStringEnumConverter(CamelCase)` (`Program.cs:125-127`). `decimal?`/`int?` serialize as JSON number-or-null. **No `DateOnly`/dates on the recipe contract** (recipes have no week boundary — the date trap doesn't apply here, but the rule still holds for any date field added later).

```csharp
// ── List ──────────────────────────────────────────────────────────────────
public sealed record RecipeListDto(
    IReadOnlyList<RecipeListItemDto> Recipes,
    IReadOnlyList<int> FavoriteRecipeIds);          // own-household only; [] for connected

public sealed record RecipeListItemDto(
    int RecipeId, string Name, RecipeType RecipeType,
    string? ImagePath, bool HasSourceUrl,           // bool, NOT the url (card only shows the icon)
    string? CreatedByName, string? CreatedByPictureUrl,   // User has PictureUrl + Initials, NO color field (User.cs) → card renders pic-or-initials; both null when connected (privacy) or deleted user
    IReadOnlyList<string> IngredientPreview,        // first 3 ingredient names (card preview)
    int IngredientCount);                           // for the "+N more" suffix

// ── Full (read drawer + edit form — superset, D3) ──────────────────────────
public sealed record RecipeFullDto(
    int RecipeId, string Name, RecipeType RecipeType,
    string? Description,
    string? Instructions,                           // RAW markdown — for the edit textarea
    string InstructionsHtml,                        // server-sanitized — for the read drawer ({@html})
    string? ImagePath, string? SourceUrl,
    int? PrepTimeMinutes, int? CookTimeMinutes, int? Servings,
    string? CreatedByName, string? CreatedByPictureUrl,  // both null when connected (includeAuthor:false) / deleted user
    string? SharedFromHouseholdName,                // attribution for copied recipes
    IReadOnlyList<RecipeIngredientFullDto> Ingredients);

public sealed record RecipeIngredientFullDto(
    int IngredientId, decimal? Quantity, string? Unit, string Name,
    string Category, string? Notes, string? GroupName, int SortOrder);

// ── Parse / categories / import / connections / draft ──────────────────────
public sealed record ParsedIngredientDto(
    decimal? Quantity, string? Unit, string Name, string? Notes,
    bool IsComplete, string SuggestedCategory);
public sealed record CategoryDto(string Name);
public sealed record ConnectedHouseholdDto(int HouseholdId, string HouseholdName);
// ↑ intentionally drops ConnectedHouseholdInfo.ConnectedAt (3rd field) — not shown in the selector.
public sealed record RecipeImportResultDto(
    bool Success, int? RecipeId, string? ErrorMessage, string? ErrorType,
    int? ExistingRecipeId, string? ExistingRecipeName,
    PartialRecipeDataDto? PartialData);
public sealed record PartialRecipeDataDto(
    string? Name, string? Description, string? Instructions,
    IReadOnlyList<string>? IngredientStrings, string? ImageUrl,
    int? PrepTimeMinutes, int? CookTimeMinutes, int? Servings);
// Drafts reuse DraftService's EXISTING records directly — `RecipeDraftData` (DraftService.cs) for
// BOTH the PUT body and the GET response, and `IngredientDraftData` for its lines. No new draft DTO:
// they have no enums and already serialize camelCase via the web defaults (fresh-eyes P1-3).
```

**Why a separate `RecipeFullDto` and NOT the meal-plan `RecipeDetailDto`** (D11): the meal-plan one is deliberately lean (board-card view); the recipes edit form needs raw markdown, description, source URL, and per-ingredient `Category`/`GroupName`/`IngredientId`. Extending the meal-plan DTO would break its just-shipped M9 contract (`MealPlanBoardDtoContractTests` + `Fixtures/MealPlanBoard/board.json` + meal-plan `types.ts`). Parity-first + minimize-churn ⇒ **keep them separate**; one richer projection for recipes, the lean one stays meal-plan's.

**TS mirror** (`frontend/recipes/src/lib/types.ts`) — camelCase keys, doc-commented:
```ts
export type RecipeType =
  'main'|'side'|'appetizer'|'dessert'|'beverage'|'sauce'|'breakfast'|'snack'|'other';
export interface RecipeListItemDto {
  recipeId:number; name:string; recipeType:RecipeType; imagePath:string|null;
  hasSourceUrl:boolean; createdByName:string|null; createdByPictureUrl:string|null;
  ingredientPreview:string[]; ingredientCount:number }
export interface RecipeListDto { recipes:RecipeListItemDto[]; favoriteRecipeIds:number[] }
export interface RecipeIngredientFullDto {
  ingredientId:number; quantity:number|null; unit:string|null; name:string;
  category:string; notes:string|null; groupName:string|null; sortOrder:number }
export interface RecipeFullDto {
  recipeId:number; name:string; recipeType:RecipeType; description:string|null;
  instructions:string|null; instructionsHtml:string; imagePath:string|null;
  sourceUrl:string|null; prepTimeMinutes:number|null; cookTimeMinutes:number|null;
  servings:number|null; createdByName:string|null; createdByPictureUrl:string|null;
  sharedFromHouseholdName:string|null; ingredients:RecipeIngredientFullDto[] }
export interface ParsedIngredientDto {
  quantity:number|null; unit:string|null; name:string; notes:string|null;
  isComplete:boolean; suggestedCategory:string }
export interface ConnectedHouseholdDto { householdId:number; householdName:string }
export interface RecipeImportResultDto {
  success:boolean; recipeId:number|null; errorMessage:string|null; errorType:string|null;
  existingRecipeId:number|null; existingRecipeName:string|null; partialData:PartialRecipeDataDto|null }
export interface RecipeDraftData {  // mirrors DraftService.RecipeDraftData — reused as the PUT body AND GET response
  name:string; description:string|null; instructions:string|null; imagePath:string|null;
  sourceUrl:string|null; servings:number|null; prepTimeMinutes:number|null; cookTimeMinutes:number|null;
  ingredients:IngredientDraftData[] }
export interface IngredientDraftData {
  name:string; quantity:number|null; unit:string|null; category:string;
  notes:string|null; groupName:string|null; sortOrder:number }
// … PartialRecipeDataDto, CategoryDto similarly
export type IslandView = 'list' | 'edit';
export interface ShellContext {
  householdId:number; userId:number; userName:string;
  view:IslandView; recipeId:number|null }   // recipeId set only for edit of an existing recipe
```

---

## 5. Decisions

**D1 — Multi-route island: one bundle, view chosen by root data-attr.** Two Razor host pages (`Recipes.razor`, `RecipeEdit.razor`) both import the **same** `/islands/recipes/index.js` and mount the same `window.RecipesIsland`; the root element carries `data-view="list"|"edit"` (+ `data-recipe-id` for edit). `main.ts` reads `view` from the root dataset and mounts the matching root component (`ListApp.svelte` or `EditApp.svelte`). *Alt considered:* a client-side SPA router inside one mount — rejected: diverges from the meal-plan/chores one-host-page pattern, and the strangler intentionally keeps the Blazor shell + routing. *Tradeoff:* cross-view transitions are full navigations (D8), not SPA pushes — acceptable for parity (today each is a separate Blazor page).

**D2 — NL ingredient parser: server endpoint, not a TS port.** `IIngredientParser.ParseIngredient` is a ~250-line pure C# function (Unicode-fraction normalization, range/fraction/unit/notes extraction) with no external deps (`IngredientParser.cs`). Porting it to TS risks parity drift on every edge case and duplicates logic. Instead, `POST /parse-ingredient` on blur/Enter (one cheap round-trip per ingredient add). *Tradeoff:* a network hop per ingredient vs offline parse — fine; ingredient adds are deliberate and infrequent, and the island is online by definition. Bulk-paste uses `POST /parse-ingredients` (one round-trip for all lines).

**D3 — Recipe detail: one superset endpoint serving both the read drawer and the edit load.** `GET /api/recipes/{id}` returns `RecipeFullDto` with **both** `instructionsHtml` (sanitized, for the drawer's `{@html}`) and raw `instructions` (for the edit textarea), plus full ingredient fields. *Alt:* two endpoints (lean detail + edit). Rejected: one projection = M9 (no drift between drawer and edit), and the extra payload (raw markdown + categories) is negligible. The drawer simply ignores the edit-only fields. *Parity note:* the list payload (`RecipeListItemDto`) is deliberately **lean** (no full ingredients/instructions), so the drawer **lazy-fetches** detail on open (endpoint #2; #16 for connected) — the meal-plan `RecipeDetailSheet` pattern. Today's Blazor drawer reuses the already-loaded full `Recipe` (`Recipes.razor:722-727`); the lazy fetch is an intentional, benign divergence (leaner list + one detail GET on click), not a regression.

**D4 — Drag-reorder: re-add `svelte-dnd-action`.** The ingredient list reorders by drag today (`IngredientList.razor` MudDropContainer). Mirror the **chores** island's `svelte-dnd-action` usage (the only island that kept it; meal-plan dropped it). Add it to `frontend/recipes/package.json`. Reorder updates `SortOrder` client-side; persistence is part of the recipe save (PUT replaces ingredients with their new order) — **no per-reorder endpoint** (parity: today reorder is in-memory until Save).

**D5 — Concurrency: versionless / last-write-wins (parity).** The `Recipe` entity HAS an xmin `[Timestamp] Version` (`Recipe.cs`), but today's `UpdateRecipeAsync` loads-and-overwrites without checking it — last-write-wins. Mirror that: no version token on the `/api/recipes` wire. *Tradeoff:* two members editing the same recipe ⇒ last save wins (a full-form clobber is more consequential than a meal-slot, but matching today is the parity bar). → **Harvested as provisional quest** "Recipe edit optimistic concurrency (xmin)" (§13).

**D6 — Image upload: multipart endpoint via the existing `IFormFile` overload.** `POST /api/recipes/images` (multipart/form-data) → `IImageService.SaveImageAsync(IFormFile, hh)` (already exists, `ImageService.cs:47`; the Blazor `IBrowserFile` overload is circuit-only and unusable from an endpoint). Returns `{ imagePath: "/uploads/{hh}/{guid}.ext" }`. The island uploads first, then includes the returned path in the create/update body — mirrors `RecipeEdit.razor:458-471`. 10 MB + jpg/png/gif/webp validation is in the service; map a validation failure to 400. Group already has `.DisableAntiforgery()`.

**D7 — URL import: scrape→create→return id (then navigate to edit).** Mirror `ImportRecipeDialog`: `POST /api/recipes/import { url }` → dup-check by `SourceUrl` (return `existingRecipeId`/`Name` if found, unless `force:true`) → `ImportFromUrlAsync` → on success `CreateRecipeAsync` → return the new `recipeId`; the island then navigates to `/recipes/edit/{id}` to review (D8). On failure, return `errorType` + `partialData` so the dialog can offer "Add Manually". *Tradeoff:* a scrape→preview-in-island→confirm flow would be nicer but is **new UX** — deferred. ≤60s (Polly: 3 retries, 30s/attempt, 60s total — `Program.cs` RecipeScraper client). The dialog shows a spinner; YouTube imports warn "this may take a moment".

**D8 — Cross-view navigation: full-document nav via `window.location.assign`.** When the island moves between views (list→edit, list→new, edit→list on save/cancel, import→edit), it sets `window.location.assign('/recipes/edit/3')` etc. Each destination is a clean island mount. *Alt:* call Blazor's global `Blazor.navigateTo` for enhanced (no-reload) nav — rejected as fragile cross-framework coupling; flagged as a possible later enhancement. *Tradeoff:* a full reload per transition (slightly heavier than Blazor's enhanced nav) — acceptable parity; robust, and sidesteps circuit-state edge cases. `[DECISION: D8 — low confidence; if reload feel is poor in WP-Verify, revisit Blazor.navigateTo.]`

**D9 — List search: server-side, debounced (parity).** Each debounced (300ms) search calls `GET /api/recipes?q=` (mirrors today's `OnDebounceIntervalElapsed`→`LoadRecipes`). Server matches name/description/type/ingredient-name (`GetRecipesAsync`). The **favorites filter** is client-side over the loaded set (mirrors `FilteredRecipes`), and the **favoriteRecipeIds** come down with the same payload (one round-trip vs Blazor's two). *Alt:* load-all-once + client filter (the shopping-list/chores "one payload" M11). Rejected: ingredient-search is already server-side, and a household's full recipe set with all ingredients could be a large payload; server-search keeps the list lean and preserves search semantics exactly. Liveness re-runs the *current* search query.

**D10 — Autosave drafts: keep `DraftService`, 2s debounce, server-side.** Mirror `RecipeEdit.razor:368-403`: a 2s debounce after edits `PUT /api/recipes/draft`; on edit-page load `GET /api/recipes/draft?recipeId=` and restore if present (toast "Restored from draft"); on successful Save/Delete, `DELETE /api/recipes/draft`. Draft JSON is stored camelCase by `DraftService` already. The nav-lock (warn on unsaved changes) maps to a `beforeunload` handler in the island (replaces Blazor `NavigationLock`).

**D11 — Keep the meal-plan `/api/meal-plan/recipes/*` endpoints + DTOs separate.** Do not relocate or merge. They serve the meal-plan picker with a lean shape and have a shipped contract test. The recipes island owns `/api/recipes/*` + `RecipeDtos.cs`. The only shared *concept* is the recipe entity; the projections differ by need. (Answers the handoff's "relocate/share vs separate" question.)

**D12 — Fix the confirmed `UpdateRecipeAsync` `RecipeType` drop (tiny, justified parity-correctness scope).** `UpdateRecipeAsync` (`RecipeService.cs:94-104`) updates every scalar **except `RecipeType`** — so a type change has been **silently ignored in production Blazor** (the edit form has a type selector, `RecipeEdit.razor:122-130`). This is a latent bug, not intended behavior; strict "parity" would replicate a broken edit. Add the one line `existing.RecipeType = recipe.RecipeType;` in WP-2 and assert it with an integration test (§9). The only other caller is the Blazor edit page, which benefits identically. (Surfaced by the council's codex lens; verified at source.)

---

## 6. Island structure — `frontend/recipes/`

Copy the meal-plan scaffold; re-scope; add `svelte-dnd-action`. Renames (from the frontend-source survey): root ids `recipes-list-root` / `recipes-edit-root`, global `window.RecipesIsland`, dark class `rc-dark-mode`, CSS prefix `rc-`, store `recipesStore`.

- **Build config:** `package.json` (name `recipes-island`; deps: svelte + vite toolchain **+ `svelte-dnd-action`**), `tsconfig.json`, `svelte.config.js`, `vite.config.ts` (port **5176**, `outDir:dist`, `input:src/main.ts`, `entryFileNames:index.js`, css→`index.css`, `/api` proxy→`:5000`), `index.html` (dev auto-mount — mount whichever root is present).
- **`src/main.ts`** — copy meal-plan verbatim; rename. **Branch on `data-view`:** `readContext` reads `view` + `recipeId` from the root dataset; `mountRoot` mounts `ListApp` for `view==='list'` else `EditApp`. Support **both** root ids (`recipes-list-root`, `recipes-edit-root`) in `desiredRoots`/auto-mount. Keep the self-heal + dark observer **unchanged**.
- **`src/lib/types.ts`** — §4.
- **`src/lib/api.ts`** — copy meal-plan's `request<T>`/`ApiError`; `BASE='/api/recipes'`; add functions for all 20 routes (`listRecipes(q)`, `getRecipe(id)`, `createRecipe(body)`, `updateRecipe(id,body)`, `deleteRecipe(id)`, `toggleFavorite(id)`, `ingredientSuggestions(prefix)`, `parseIngredient(text)`, `parseIngredients(lines)`, `getCategories()`, `uploadImage(file)` [FormData, no JSON content-type], `listImages()`, `importRecipe(url,force)`, `getConnections()`, `listConnectedRecipes(chId,q)`, `getConnectedRecipe(chId,id)`, `copyConnectedRecipe(chId,id)`, `getDraft(recipeId?)`, `saveDraft(body)`, `deleteDraft(recipeId?)`). Any 4xx ⇒ `ApiError` (caller reconciles + calm toast).
- **`src/lib/liveness.ts`** — copy verbatim. Wired **only** in `ListApp` (the edit form is single-user — no poll; autosave-drafts is the persistence path there).
- **`src/lib/toasts.svelte.ts` + `components/Toasts.svelte`** — copy + re-scope `rc-`.
- **`src/lib/quantity.ts`** — NEW: port the quantity formatters + scaling math. **Two distinct formatters — do NOT merge them:** (a) `formatScaledQuantity` for the drawer's *scaled* values — RANGE thresholds (`Recipes.razor:897-905`): ¼=`[0.2,0.3)`, ⅓=`[0.3,0.4)`, ½=`[0.45,0.55)`, ⅔=`[0.6,0.7)`, ¾=`[0.7,0.8)`, whole when `==floor`, else `0.##`; (b) `formatExactQuantity` for the ingredient-list display — EXACT-equality checks (`IngredientList.razor:106-132`): `0.25→"1/4"`, `0.5→"1/2"`, `0.75→"3/4"`, `1/3`, `2/3`, plus mixed-number forms (`"1 1/2"`), else `0.##`. Scaling: `getScalingFactor = scaledServings/servings`, `getScaledQuantity = qty*factor` (`Recipes.razor:875-885`). Client-only, no endpoint.
- **`src/lib/recipeListStore.svelte.ts`** + **`src/lib/recipeEditStore.svelte.ts`** — TWO separate files (one class-instance store each — Svelte-5 rune rule: export the instance, never a reassigned `$state`). **Split across two files so WP-5 (list) and WP-6 (edit) own disjoint files** — no shared-file collision (council).
  - **`RecipeListStore`** (`recipeListStore.svelte.ts`) — `query`, `recipes = $state<RecipeListItemDto[]>`, `favoriteIds = $state<Set<number>>`, `showFavoritesOnly`, `selectedConnectedId = $state<number|null>`, `connections`, `loading`, `error`, `currentUserId`. `displayed = $derived` (favorites filter over `recipes`). `load()` — `selectedConnectedId==null` ⇒ GET #1, set `recipes`+`favoriteIds`; else GET #15 (also a `RecipeListDto`, `favoriteRecipeIds` always `[]`), set `recipes`, clear `favoriteIds`. The favorites chip + per-card heart are **hidden whenever `selectedConnectedId!=null`**. `search(q)`, `toggleFavorite(id)` (optimistic Set toggle + POST + reconcile-on-error; disabled in connected mode), `setRefresh`/`reconcile` (liveness + error reconcile, like meal-plan). **All HTTP awaited; reconcile re-runs the current query.**
  - **`RecipeEditStore`** (`recipeEditStore.svelte.ts`) — `form = $state<RecipeEditModel>`, `ingredients = $state<RecipeIngredientFullDto[]>`, `loading`, `saving`, `dirty`, `autosaveStatus = $state<'none'|'saving'|'saved'>`, **`error = $state<string|null>`** (the save/delete failure path §8). `load(recipeId|null)` (draft-first then recipe-or-blank, mirror `RecipeEdit.razor:297-360`); `addIngredient`/`addBulk`/`removeIngredient`(undo)/`reorder`(updates `sortOrder`); `scheduleAutosave()` (2s debounce → `saveDraft` with current `form`+`ingredients`); `save()`/`delete()` → **on any 4xx (incl. the 404-as-400 quirk): set `error`, toast "this recipe changed", `window.location.assign('/recipes')`** (concurrent delete / last-write-wins, §8); **on success:** `deleteDraft` + `window.location.assign('/recipes')` (D8). `uploadImage(file)` → endpoint #11 → set `form.imagePath`.
    - **`RecipeEditModel`** + the **form→`RecipeWriteRequest` mapper** (made explicit per the council): `{ name, description, instructions, sourceUrl, prepTimeMinutes, cookTimeMinutes, servings, recipeType, imagePath }`. On save: **trim `name`; block save if blank** (mirror `RecipeEdit.razor:506-510`); **blank strings → null** for optional text fields; new-recipe defaults `recipeType='main'`, `servings=4` (`RecipeEdit.razor:352-357`); each ingredient carries `category` (fallback to the first category, else `'Pantry'`) and a **recomputed `sortOrder`** (0..n in list order); `ingredientId` is server-assigned (send `0` on create).
- **`src/ListApp.svelte`** — read `ShellContext` from `recipes-list-root` data-attrs; the **`untrack()` one-time load** (§ Loop safety); render header (Add / Import buttons), connected-household chip selector, search field, favorites chip, the card grid (`RecipeCard`), the detail **drawer** (`RecipeDetailDrawer`), delete confirm (`ConfirmDialog`), import dialog (`ImportDialog`), `Toasts`; wire `liveness` → `reconcile`.
- **`src/EditApp.svelte`** — read context (incl. `recipeId`) from `recipes-edit-root`; the **`untrack()` one-time load** calls `store.load(recipeId)`; render the form (`BasicInfoSection`, `ImageSection`, `IngredientEntry`, `IngredientList`, instructions textarea), the autosave indicator, image picker (`ImagePickerDialog`), bulk-paste (`BulkPasteDialog`), Save/Cancel/Delete; install the `beforeunload` nav-lock when `dirty`. **No liveness** (single-user form).
- **`src/lib/components/`** (re-scope `rc-`, Svelte-5 `$props`/`$state`/`$derived`):
  - `RecipeCard.svelte` — image/placeholder, name, type chip, "imported" icon, author avatar (or "deleted user"), ingredient preview, favorite heart (hidden when read-only/connected). Click → open drawer.
  - `RecipeDetailDrawer.svelte` — slide-out + scrim. Lazy detail fetch on open — `getRecipe(id)` (#2) for own recipes, **`getConnectedRecipe(selectedConnectedId, id)` (#16) in connected mode**. Image, author, prep/cook chips, **servings scaler** (+/- → rescale ingredient qty via `quantity.ts`), description, ingredients, `{@html} instructionsHtml`, source link (safe-url guard), actions (Edit→nav / Add-to-Meal-Plan stub toast / Delete→confirm; or **Copy** for connected). Mirror `Recipes.razor:223-416`.
  - `ConnectedHouseholdSelector.svelte` — "My Recipes" + a chip per connection + a "manage connections" link. Mirror `Recipes.razor:55-79`.
  - `ImportDialog.svelte` — URL field, supported-sites note, spinner (YouTube variant), dup-warning ("View Existing" / "Import Anyway"=force), error + "Add Manually". Mirror `ImportRecipeDialog.razor`. On success → nav to `/recipes/edit/{id}`.
  - `ConfirmDialog.svelte` — copy meal-plan's (delete confirm).
  - `BasicInfoSection.svelte` / `ImageSection.svelte` — form fields + image upload/remove/"choose existing".
  - `IngredientEntry.svelte` — raw input; on Enter/Tab → `parseIngredient` → reveal editable qty/unit/name/category/notes; unit autocomplete (static list, `IngredientEntry.razor:103-107`); name autocomplete (`ingredientSuggestions`); category select (`getCategories`); "Bulk Paste" button → `BulkPasteDialog`. Mirror `IngredientEntry.razor`.
  - `IngredientList.svelte` — **`svelte-dnd-action`** reorder (updates `sortOrder`), edit (remove+re-add), delete-with-undo toast, category chip + color. Mirror `IngredientList.razor`.
  - `ImagePickerDialog.svelte` — grid of `listImages()`, select, confirm. Mirror `ImagePickerDialog.razor`.
  - `BulkPasteDialog.svelte` — textarea → `parseIngredients(lines)` → preview rows (qty/unit/name/notes, incomplete warning) → import. Mirror `BulkPasteDialog.razor`.
- **`src/styles/{tokens,app}.css`** — copy meal-plan; re-scope `rc-`/`#recipes-list-root,#recipes-edit-root` + `rc-dark-mode`. **No `dates.ts`** (recipes have no week math).

---

## 7. Host pages + build wiring

- **`Components/Pages/Recipes.razor`** — mirror `MealPlan.razor`. Flag **`RECIPES_USE_ISLAND`** (same `IsIslandEnabled()` config-or-env helper, `MealPlan.razor:98-108`). **ON →** `<div id="recipes-list-root" data-household-id data-user-id data-user-name data-view="list">`, `<HeadContent>` `index.css?v=`, `OnAfterRenderAsync` `ensureIslandStylesheet` + `import('/islands/recipes/index.js?v=')` + `RecipesIsland.mount("recipes-list-root")`, `IslandVersion` cache-bust, `IAsyncDisposable`+`JSDisconnectedException` teardown calling `RecipesIsland.destroy`. **OFF →** the existing Blazor body (keep `IRecipeService`/dialogs intact as the zero-regression fallback; delete in a later cleanup).
- **`Components/Pages/RecipeEdit.razor`** — same, for both `@page "/recipes/new"` and `@page "/recipes/edit/{RecipeId:int}"`. ON → `<div id="recipes-edit-root" data-household-id="@_shell.HouseholdId" data-user-id="@_shell.UserId" data-user-name="@_shell.UserName" data-view="edit" data-recipe-id="@(RecipeId?.ToString() ?? "")">` + mount `"recipes-edit-root"`. OFF → existing Blazor edit form. (Same full data-attr set as the list root — identity is server-resolved per M1, but the island still reads the shell context.)
- **`CopyRecipesIsland`** MSBuild target in `FamilyCoordinationApp.csproj` — copy of `CopyMealPlanIsland` (`.csproj:56-71`): `frontend/recipes/dist/**` → `wwwroot/islands/recipes/`, `BeforeTargets="AssignTargetPaths"`, `Condition="Exists('…/frontend/recipes/dist')"`, `SkipUnchangedFiles`.
- **Dockerfile** — edit the **multi-stage `Dockerfile`** (used by prod deploy + CI): add a `recipes-node-build` stage (copy of `mealplan-node-build`, `Dockerfile:20-27`): `node:20-alpine`, `npm ci`/`npm install`, `npm run build` in `frontend/recipes/`; then `COPY --from=recipes-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/recipes/` in the .NET build stage (next to `Dockerfile:41`). **Do NOT touch `Dockerfile.runtime-only`** (the local `docker-build.sh` path) — it has no node stages and relies on `docker-build.sh`'s `build_island` + the `CopyRecipesIsland` MSBuild target during `dotnet publish`.
- **`docker-build.sh`** — add `build_island "./frontend/recipes" "recipes"` (after the meal-plan line, `docker-build.sh:~64`).
- **`Program.cs`** — `app.MapRecipesEndpoints();` (after `MapMealPlanEndpoints()`, `:401`); register `IRecipeProjectionService`. Already registered: core services `:83-95`; `IRecipeImportService` + scraper + the `RecipeScraper` HttpClient/Polly `:139-159`; `IHouseholdConnectionService` `:95`. Confirm `JsonStringEnumConverter(CamelCase)` is registered (it is, `:125-127`).
- **Feature flag** — add `RECIPES_USE_ISLAND` to `docker-compose.yml` `app.environment` (`${RECIPES_USE_ISLAND:-false}`, next to the other three) and the repo's **`.env`** (local dev = `true`; the three existing island flags live there, `.env:19,21,26` — none are in `.env.example`, so match that). **Prod** flips it in the host's `~/familyapp/.env.local` (deploy regenerates `.env` from it; per the kickoff handoff) — ship **OFF** first, flip after verify. There is **no** `.env.local` in the repo.
- **`ensureIslandStylesheet` / `import` glue** — reuse the existing `window.ensureIslandStylesheet` in `Components/App.razor:47-64`; do not duplicate.

---

## 8. Concurrency — versionless / last-write-wins

Mirrors meal-plan + parity (D5). No version/xmin on the `/api/recipes` wire. Conflict story: two members editing the same recipe → last save wins; the list's 20s liveness reconciles the *card grid*, and the edit form's autosave-draft is per-user (no cross-user merge). Deleting/editing an already-deleted recipe → 404/empty-400 → the island reconciles (list refetch) or surfaces a calm "this recipe changed" toast and returns to the list. Matches today's Blazor (`UpdateRecipeAsync` overwrites; `DataNotifier` last-write-wins).

---

## 9. Contract & integration tests (M9)

- **`RecipeListDtoContractTests`** (mirror `MealPlanBoardDtoContractTests`): build a representative `RecipeListDto` (recipes with/without image, with/without source URL, with ingredient previews + counts, varied `RecipeType`, one with `createdByName` null = deleted user, plus a non-empty `favoriteRecipeIds`); serialize with the **same global options** (`JsonSerializerDefaults.Web` + `JsonStringEnumConverter(CamelCase)`, `WriteIndented`); assert byte-equality against checked-in `Fixtures/RecipeList/list.json`. The island `types.ts` mirrors the fixture.
- **`RecipeFullDtoContractTests`** + `Fixtures/RecipeFull/recipe.json`: a recipe with ingredients incl `category`/`groupName`/`ingredientId`, both `instructions` (raw) + `instructionsHtml` (sanitized), `sharedFromHouseholdName` set, optional fields null — to lock the edit/drawer superset shape + camelCase enum.
- **Endpoint integration (real Postgres, mirror chores/meal-plan endpoint tests):** list (empty + populated + with `q` matching name vs ingredient); detail round-trip; **create** → list reflects it + ids assigned; **update** replaces ingredients + order; **delete** soft-deletes (gone from list, `IsDeleted` row remains); favorite **toggle** flips both ways; ingredient-suggestions (`<2` chars ⇒ `[]`); parse-ingredient + parse-ingredients shape; image upload (multipart) returns a path + the file lands under `/uploads/{hh}/`; import dup-detection (returns existing) + `force`; categories; connections list; connected list/detail/copy; draft get/put/delete round-trip; **`update` changes `recipeType`** (regression-guards D12) and the **PUT response body reflects the new ingredients/order** (catches the stale-return trap — proves the endpoint projects from a re-fetch); **favorite / update / delete of a missing or cross-household id ⇒ 404, not 500**; **cross-household isolation (M1)** — a user in household A cannot read/update/delete/favorite/copy household B's recipe, and cannot read B's images/drafts/connected recipes without a real connection (403 on unconnected `/connected/*`).

---

## 10. Loop safety (⚠ the #1 regression risk — bake in from day one)

Memory `svelte5-setup-effect-async-loader-loop`: a one-time setup `$effect` that synchronously reads `$state`/`$derived` it (transitively) writes subscribes to that state → infinite fetch→write→re-run loop (~30–46 req/s). It broke meal-plan visibly and chores silently (PR #52). **Both** `ListApp.svelte` and `EditApp.svelte` have a setup effect that calls a loader (`store.load(...)`) whose sync prefix reads then writes store state.

**Fix preemptively** — copy the meal-plan `App.svelte` pattern verbatim: wrap the **entire** setup-effect body in `untrack(() => { … })` so it has zero reactive deps and runs exactly once; liveness (list) + user actions drive later refreshes:
```svelte
import { untrack } from 'svelte';
$effect(() => {
  untrack(() => {
    store.init(ctx);
    store.setRefresh(load);   // (list)
    load();                   // reads then writes store state — MUST be inside untrack
    liveness = startLiveness(() => store.reconcile());  // (list only)
  });
  return () => { liveness?.stop(); /* + beforeunload cleanup on edit */ };
});
```

**MANDATORY GATE (WP-Verify):** open each view on `:8080`, install a `fetch` counter in the console, confirm the list/detail/draft endpoints do **~0 background GETs over several seconds** (initial load + 20s liveness on the list only) — **NOT tens/sec.** Re-check on prod after flipping the flag.

---

## 11. Gotchas to carry (from shopping-list + chores + meal-plan)

1. **Empty-body 4xx quirk** — `UseStatusCodePagesWithReExecute` re-executes empty-body 404s as empty **400s** (memory `fca-empty-404-surfaces-as-405-on-delete`; a bare `Results.NotFound()` on DELETE even surfaces as **405**). Every not-found returns a **non-empty body** (`Results.NotFound(new { message })`). Island treats any 4xx as a non-retryable rejection → reconcile + calm toast.
2. **Casing** — `RecipeType` is a real enum on the DTO ⇒ **camelCase** via the global converter (`"main"`, `"side"`, …). `decimal? Quantity` ⇒ JSON number-or-null. Document at the top of `types.ts`.
3. **Multi-tenant (M1)** — household/user always server-resolved via `UserContextResolver`; every query filters `HouseholdId`; cross-household integration tests mandatory; connected-household reads require a validated connection (403 otherwise).
4. **Image upload is multipart, not JSON** — endpoint #11 binds `IFormFile`; the island sends `FormData` (no `Content-Type: application/json` — let the browser set the multipart boundary). Use the existing `SaveImageAsync(IFormFile,…)` overload (`ImageService.cs:47`), not the `IBrowserFile` one.
5. **Drafts reuse the existing service records** — `DraftService` already serializes camelCase; the endpoints take/return `RecipeDraftData` **directly** (no new C# draft DTO, P1-3), and the island's `RecipeDraftData`/`IngredientDraftData` TS mirror must match those records field-for-field so a draft round-trips.
6. **Soft delete + EF global query filter** — `DeleteRecipeAsync` sets `IsDeleted` (row persists; no hard delete). `Recipe` has an **EF global query filter** `HasQueryFilter(r => !r.IsDeleted)` (`RecipeConfiguration.cs:45`), so deleted recipes are auto-excluded from every list/detail query — endpoints need **no** explicit `!IsDeleted`. (A fresh-eyes pass flagged the service methods as unfiltered; the *global* filter covers them — verified.) `CreateRecipeAsync`'s next-id calc uses `IgnoreQueryFilters()` so ids still bump past deleted rows; the import dup-check's explicit `!IsDeleted` is belt-and-suspenders.
7. **Import is slow + external** — ≤60s (Polly), SSRF-guarded server-side (`IUrlValidator`), YouTube branch uses yt-dlp. The island just shows a spinner and handles the result DTO; do **not** add a client fetch timeout shorter than 60s.
8. **Island resilience** — reuse `main.ts`'s mount/heal/dark-mode self-heal verbatim (the `.NET 10` circuit-resume root-replacement fix). Load-bearing; don't reinvent. Extend only to handle the two root ids / `data-view` branch.
9. **DbContextFactory** — services inject `IDbContextFactory<ApplicationDbContext>`, short-lived contexts (already true of every service the endpoints touch).
10. **Servings scaler precision** — port the EXACT thresholds (§6 `quantity.ts`): the drawer's *scaled* formatter uses **ranges** (¼=`[0.2,0.3)`…¾=`[0.7,0.8)`, `Recipes.razor:897-905`); the edit list's formatter uses **exact-equality** (`IngredientList.razor:106-132`). They differ because scaled values are fuzzy and entered values are exact — keep both. Porting from intuition diverges.
11. **`UpdateRecipeAsync` return is stale + drops `RecipeType`** (verified, `RecipeService.cs:94-132`) — the PUT endpoint must (a) add `existing.RecipeType = recipe.RecipeType;` (D12) and (b) project its response from a fresh `GetRecipeAsync(hh, recipeId)`, **not** the service return (whose `Ingredients` nav still holds the RemoveRange'd rows). Update/delete also **throw** `InvalidOperationException` on missing → catch → non-empty 404.

---

## 12. Build sequence (work packages)

> **Format note:** this is a single consolidated spec (the project's precedent — cf. `.planning/meal-plan-island-spec.md`), not a multi-file workshop. The executor reads the **whole doc**, so each WP below is a build phase, not a standalone file; cross-references ("§3 #4", "D12") resolve within this document, and file ownership across WPs is disjoint (the §6 store split is the one place that mattered). The council's "split into `_orchestrator.md`/`wp-*.md`" note is a workshop-format preference that doesn't apply here.

- **WP-1 — Backend DTOs + projection.** `Services/Dtos/RecipeDtos.cs` (§4), `IRecipeProjectionService`+impl (`ToListItem`/`ToFull`/`ToParsed`; `ToFull` uses `MarkdownHelper.ToSafeHtml` for `instructionsHtml`). No endpoints yet. Builds clean.
- **WP-2 — Backend endpoints.** `Endpoints/RecipesEndpoints.cs` (all 20 routes, §3), request DTOs, M1 via `UserContextResolver`, multipart image upload, import dup-logic, connection validation on `/connected/*`, non-empty 404 bodies. `Program.cs` wiring (`MapRecipesEndpoints`, register `IRecipeProjectionService`). **Carry the fresh-eyes fixes:** import = `ImportFromUrlAsync`→`CreateRecipeAsync`→return the assigned id (the result's `Recipe` is unsaved, P1-2); `parse-ingredient` guards empty *before* calling (it throws, P2-2); drafts deserialize into / return `RecipeDraftData` directly — no new DTO (P1-3); connected detail = `GetRecipeAsync(chId,…)` + `ToFull(…, includeAuthor:false)` after the connection guard (P1-4). No explicit `!IsDeleted` needed (global query filter, §11.6). **Carry the council fixes:** this WP also edits **`Services/RecipeService.cs`** to add `existing.RecipeType = recipe.RecipeType;` in `UpdateRecipeAsync` (D12); the PUT endpoint projects from a **re-fetch** (`GetRecipeAsync`), not the `UpdateRecipeAsync` return (stale ingredients nav); update/delete **catch `InvalidOperationException`** → non-empty 404; favorite **pre-checks `GetRecipeAsync`** → 404 before toggling; connected list/copy **validate the connection** (`AreHouseholdsConnectedAsync`) → 403.
- **WP-3 — Backend tests.** `RecipeListDtoContractTests` + `RecipeFullDtoContractTests` + fixtures; endpoint integration (real PG) incl. cross-household M1 + connected-household 403 + image-file-lands + draft round-trip (§9).
- **WP-4 — Island scaffold.** `frontend/recipes/` config (`+svelte-dnd-action`), `types.ts`, `api.ts` (20 fns), `main.ts` (two-root `data-view` branch + self-heal), `liveness.ts`/`toasts*`/`styles` (copied + re-scoped from meal-plan), **`quantity.ts` (NEW — port the two formatters, §6)**, `ListApp.svelte`+`EditApp.svelte` skeletons. The two stores are created in WP-5/WP-6 (disjoint files: `recipeListStore.svelte.ts` / `recipeEditStore.svelte.ts`). `svelte-check` 0/0, `vite build` green.
- **WP-5 — Island list view.** `RecipeListStore`, `ListApp` (the **`untrack` load**), `RecipeCard`, `ConnectedHouseholdSelector`, search + favorites, `RecipeDetailDrawer` (servings scaler), `ImportDialog`, `ConfirmDialog`, liveness. `svelte-check` 0/0, `vite build`.
- **WP-6 — Island edit view.** `RecipeEditStore`, `EditApp` (the **`untrack` load**), `BasicInfoSection`/`ImageSection`, `IngredientEntry` (parse-on-blur), `IngredientList` (**dnd reorder**), `BulkPasteDialog`, `ImagePickerDialog`, instructions textarea, autosave drafts + `beforeunload` nav-lock, Save/Cancel/Delete. `svelte-check` 0/0, `vite build`.
- **WP-7 — Host pages + build wiring.** `Recipes.razor` + `RecipeEdit.razor` flag + mount (keep Blazor fallback), `CopyRecipesIsland`, Dockerfile `recipes-node-build` stage, `docker-build.sh` entry, `RECIPES_USE_ISLAND` in compose + `.env`/`.env.local`.
- **WP-8 — Verify.** Browser-verify on `:8080` (rebuild `familyapp:latest`, swap app container, DOM+psql — Chrome screenshots flake; memory `fca-local-browser-verify-recipe`); flip `RECIPES_USE_ISLAND=true`; exercise list/search/favorites/connected/detail+scaler/import + new/edit/ingredient-parse/drag/bulk-paste/image-upload+picker/autosave/delete; **the LOOP-CHECK gate** (§10) on both views.

---

## 13. Gates (chores/meal-plan precedent; worktree baseline = memory `fca-worktree-gate-baseline`)

- `dotnet build` clean.
- `dotnet test` full suite green incl. new contract + integration tests. *Worktree baseline:* 2 `ApiKeySecurityTests` fail (`.git` is a file) + 49 pre-existing `dotnet format` whitespace errors on master — "green" = **no NEW failures**.
- `svelte-check` 0 errors / 0 warnings in `frontend/recipes/`.
- `vite build` → `dist/index.js` + `dist/index.css`.
- Real-Postgres integration for the new endpoints passes (incl. cross-household M1 + connected 403).
- **Loop-check** (§10): both views ~0 background GETs/sec.
- Browser-verify on `:8080` (both views, flag ON) confirms parity.

---

## 14. Provisional quests to harvest (don't build now — author in the Spine)

- **Recipe edit optimistic concurrency (xmin).** Use the existing `Recipe.Version` to detect concurrent full-form edits (409 → "this recipe changed, reload"). Today (and this island, D5) is last-write-wins.
- **Import scrape→preview-in-island.** Show the parsed result before creating, instead of create-then-edit (D7). New UX.
- **Enhanced (no-reload) cross-view nav via `Blazor.navigateTo`.** Replace the full-document `window.location` transitions (D8) if the reload feel is poor.
- **Inline ingredient edit.** Today edit = remove + re-add (`IngredientList.razor:451-456`); a proper inline editor is new UX.
- **Delete the Blazor recipe pages + dialogs.** Cleanup once the island is prod-stable (mirrors the meal-plan fallback-removal follow-up).

---

## 15. Open items (non-blocking — resolve in-build)

- **D8 reload feel** — confirm full-document nav between views is acceptable at WP-8; else harvest the `Blazor.navigateTo` quest.
- **Exact Dockerfile stage placement / COPY line** — confirm against the real Dockerfile at WP-7 (the survey gives `Dockerfile:20-27,41`).
- ~~`createdByAvatarColor`~~ **RESOLVED (fresh-eyes P2-1):** `User` has no color field (only `PictureUrl`/`Initials`, `User.cs`) → DTO carries `createdByPictureUrl` + `createdByName`; card renders pic-or-initials.
- ~~Connected-household detail~~ **RESOLVED:** the island lazy-fetches detail for BOTH own (#2) and connected (#16) recipes (lean list + detail-on-open, D3 parity note); today's Blazor reuses the loaded object — benign divergence.
