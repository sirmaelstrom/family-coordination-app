# Phase 03: Meal Planning - Research

**Researched:** 2026-01-23
**Domain:** Blazor Server weekly meal plan UI with calendar and list views
**Confidence:** MEDIUM

## Summary

Research focused on implementing weekly meal planning in Blazor Server using MudBlazor for UI. The phase requires two view modes: a 7-day grid calendar (desktop) and a list view (mobile). The existing data model already supports this: `MealPlan` represents a week, `MealPlanEntry` represents individual meals with `DateOnly`, `MealType` enum, optional `RecipeId`, and `CustomMealName` for non-recipe meals.

Key finding: MudBlazor does NOT have a native calendar component. Options are: (1) **Heron.MudCalendar** - a community library compatible with MudBlazor, or (2) **Custom CSS Grid** - build a simple 7-column grid using MudGrid/MudPaper. Given the meal plan's simple requirements (fixed 7-day grid, 3 meal slots per day), a custom grid is simpler and avoids external dependencies. The grid cells are clickable slots that open a recipe picker dialog.

**Primary recommendation:** Build a custom weekly grid using CSS Grid with MudPaper cells. Use `MudHidden` for responsive view switching between grid (desktop) and list (mobile). Use `MudDialog` with `MudAutocomplete` for recipe selection. Reuse existing patterns (expand-in-place, IDbContextFactory, soft delete) from Phase 2.

## Standard Stack

The established libraries/tools for Blazor meal planning:

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MudBlazor | 7.x+ | UI components (Grid, Paper, Dialog, Hidden) | Already in use, provides responsive grid, dialogs, breakpoint detection |
| Microsoft.AspNetCore.Components | 10.x | Blazor framework | Existing project framework |
| Microsoft.EntityFrameworkCore | 10.x | Data access | Existing patterns with IDbContextFactory |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Heron.MudCalendar | 3.3.0 | MudBlazor calendar extension | Only if complex calendar features needed (month view, drag events). NOT recommended for Phase 3 simple grid. |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Custom CSS Grid | Heron.MudCalendar | Calendar library adds dependency, has more features than needed. Custom grid is ~100 lines of CSS, simpler for 7-day fixed layout |
| Custom CSS Grid | Radzen Scheduler | Radzen components mix poorly with MudBlazor styling. Not recommended. |
| MudDialog recipe picker | Full-page recipe selection | Dialog keeps context, avoids navigation. Full page loses meal plan visibility. Dialog is better UX. |
| MudAutocomplete | MudSelect | Autocomplete scales to 100+ recipes with search. MudSelect loads all items upfront, slower for large lists |

**Installation:**
No additional packages needed. MudBlazor already installed.

## Architecture Patterns

### Recommended Project Structure

```
Components/
├── Pages/
│   └── MealPlan.razor              # Main meal plan page (calendar + list)
├── MealPlan/
│   ├── WeeklyCalendarView.razor    # 7-day grid (desktop)
│   ├── WeeklyListView.razor        # Day-by-day list (mobile)
│   ├── MealSlot.razor              # Single meal slot (shared component)
│   ├── RecipePickerDialog.razor    # Dialog to select recipe or custom meal
│   └── MealPlanNavigation.razor    # Week prev/next buttons, date display
Services/
└── MealPlanService.cs              # CRUD operations for meal plans
```

### Pattern 1: Weekly Grid with CSS Grid

**What:** Build 7-column grid using CSS Grid. Each column is a day. Each row is a meal type (Breakfast, Lunch, Dinner). Cells are clickable MudPaper components.

**When to use:** Fixed weekly layout where days and meal slots are predictable.

**Example:**
```razor
@* WeeklyCalendarView.razor *@
<div class="meal-plan-grid">
    <!-- Header row: day names -->
    <div class="grid-header">
        @foreach (var day in WeekDays)
        {
            <div class="day-header">
                <MudText Typo="Typo.subtitle2">@day.DayOfWeek.ToString().Substring(0, 3)</MudText>
                <MudText Typo="Typo.caption" Color="Color.Secondary">@day.ToString("MMM d")</MudText>
            </div>
        }
    </div>

    <!-- Meal rows -->
    @foreach (var mealType in new[] { MealType.Breakfast, MealType.Lunch, MealType.Dinner })
    {
        <div class="meal-row">
            <div class="meal-label">
                <MudText Typo="Typo.caption">@mealType.ToString()</MudText>
            </div>
            @foreach (var day in WeekDays)
            {
                <MealSlot Date="@day"
                          MealType="@mealType"
                          Entry="@GetEntry(day, mealType)"
                          OnClick="@(() => OpenRecipePicker(day, mealType))" />
            }
        </div>
    }
</div>

<style>
    .meal-plan-grid {
        display: grid;
        grid-template-columns: 60px repeat(7, 1fr);
        gap: 4px;
    }

    .grid-header {
        display: contents;
    }

    .day-header {
        text-align: center;
        padding: 8px;
    }

    .meal-row {
        display: contents;
    }

    .meal-label {
        display: flex;
        align-items: center;
        justify-content: center;
        writing-mode: vertical-rl;
        text-orientation: mixed;
    }
</style>
```

### Pattern 2: Responsive View Switching with MudHidden

**What:** Show grid on desktop (md+), show list on mobile (sm and below). Both views share data, just different layouts.

**When to use:** Same data needs different visual representation based on screen size.

**Example:**
```razor
@* MealPlan.razor *@
<MudContainer MaxWidth="MaxWidth.ExtraLarge" Class="py-4">
    <MealPlanNavigation @bind-WeekStartDate="_weekStartDate" />

    <!-- Desktop: Calendar Grid -->
    <MudHidden Breakpoint="Breakpoint.Sm">
        <WeeklyCalendarView WeekStartDate="@_weekStartDate"
                            Entries="@_entries"
                            OnEntryClick="HandleEntryClick" />
    </MudHidden>

    <!-- Mobile: List View -->
    <MudHidden Breakpoint="Breakpoint.Md" Invert="true">
        <WeeklyListView WeekStartDate="@_weekStartDate"
                        Entries="@_entries"
                        OnEntryClick="HandleEntryClick" />
    </MudHidden>
</MudContainer>
```

### Pattern 3: Recipe Picker Dialog with Return Value

**What:** Dialog opens with recipe autocomplete. User can select existing recipe OR enter custom meal name. Dialog returns selected recipe ID or custom meal name.

**When to use:** Assigning recipes to meal slots without navigating away.

**Example:**
```razor
@* RecipePickerDialog.razor *@
@inject MudDialogInstance MudDialog

<MudDialogContent>
    <MudTabs>
        <MudTabPanel Text="Pick Recipe">
            <MudAutocomplete T="Recipe"
                             Label="Search recipes"
                             @bind-Value="_selectedRecipe"
                             SearchFunc="SearchRecipes"
                             ToStringFunc="@(r => r?.Name ?? "")"
                             DebounceInterval="300"
                             MinCharacters="0">
                <ItemTemplate Context="recipe">
                    <div class="d-flex align-center gap-2">
                        @if (!string.IsNullOrWhiteSpace(recipe.ImagePath))
                        {
                            <MudImage Src="@recipe.ImagePath" Width="40" Height="40" ObjectFit="ObjectFit.Cover" />
                        }
                        <MudText>@recipe.Name</MudText>
                    </div>
                </ItemTemplate>
            </MudAutocomplete>
        </MudTabPanel>
        <MudTabPanel Text="Custom Meal">
            <MudTextField @bind-Value="_customMealName"
                          Label="Custom meal name"
                          Placeholder="e.g., Leftovers, Eating out" />
        </MudTabPanel>
    </MudTabs>
</MudDialogContent>
<MudDialogActions>
    <MudButton OnClick="Cancel">Cancel</MudButton>
    <MudButton Color="Color.Primary" OnClick="Submit" Disabled="@(!CanSubmit)">Add</MudButton>
</MudDialogActions>

@code {
    [CascadingParameter] MudDialogInstance MudDialog { get; set; } = default!;

    private Recipe? _selectedRecipe;
    private string? _customMealName;

    private bool CanSubmit => _selectedRecipe != null || !string.IsNullOrWhiteSpace(_customMealName);

    private void Submit()
    {
        var result = new MealSelection
        {
            RecipeId = _selectedRecipe?.RecipeId,
            CustomMealName = string.IsNullOrWhiteSpace(_selectedRecipe?.Name) ? _customMealName : null
        };
        MudDialog.Close(DialogResult.Ok(result));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

### Pattern 4: Week Navigation with DateOnly

**What:** Navigate between weeks using prev/next buttons. Calculate week start (Monday) from any date.

**When to use:** Weekly views that need navigation.

**Example:**
```csharp
// MealPlanService.cs
public DateOnly GetWeekStartDate(DateOnly date)
{
    // Get Monday of the week containing the date
    var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
    return date.AddDays(-diff);
}

public DateOnly[] GetWeekDays(DateOnly weekStart)
{
    return Enumerable.Range(0, 7)
        .Select(i => weekStart.AddDays(i))
        .ToArray();
}

// Navigation component
@code {
    [Parameter] public DateOnly WeekStartDate { get; set; }
    [Parameter] public EventCallback<DateOnly> WeekStartDateChanged { get; set; }

    private async Task PreviousWeek()
    {
        await WeekStartDateChanged.InvokeAsync(WeekStartDate.AddDays(-7));
    }

    private async Task NextWeek()
    {
        await WeekStartDateChanged.InvokeAsync(WeekStartDate.AddDays(7));
    }

    private async Task GoToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = GetWeekStartDate(today);
        await WeekStartDateChanged.InvokeAsync(weekStart);
    }
}
```

### Pattern 5: Get-or-Create MealPlan for Week

**What:** When loading a week, get existing MealPlan or create new one. Prevents orphan entries.

**When to use:** Always when loading meal plan data for a week.

**Example:**
```csharp
// MealPlanService.cs
public async Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStartDate, CancellationToken ct = default)
{
    await using var context = await _dbFactory.CreateDbContextAsync(ct);

    var mealPlan = await context.MealPlans
        .Include(mp => mp.Entries)
            .ThenInclude(e => e.Recipe)
        .FirstOrDefaultAsync(mp => mp.HouseholdId == householdId
                                && mp.WeekStartDate == weekStartDate, ct);

    if (mealPlan == null)
    {
        mealPlan = new MealPlan
        {
            HouseholdId = householdId,
            MealPlanId = await GetNextMealPlanIdAsync(context, householdId, ct),
            WeekStartDate = weekStartDate,
            CreatedAt = DateTime.UtcNow,
            Entries = new List<MealPlanEntry>()
        };
        context.MealPlans.Add(mealPlan);
        await context.SaveChangesAsync(ct);
    }

    return mealPlan;
}
```

### Anti-Patterns to Avoid

- **Navigation to separate page for recipe selection:** Loses context of meal plan. User must navigate back and re-find the slot. Dialog is better UX.
- **Loading all recipes upfront for picker:** Doesn't scale. Use MudAutocomplete with async search.
- **Storing MealPlanEntry without parent MealPlan:** Creates orphan data. Always get-or-create parent first.
- **Using DateTime instead of DateOnly for meal dates:** Time component is meaningless for meal dates, adds confusion.
- **Complex drag-and-drop for meal assignment:** Overkill for initial version. Click-to-assign is simpler, works on mobile.

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Responsive breakpoint detection | JavaScript interop | MudHidden with Breakpoint | MudHidden handles SSR correctly, no JS overhead |
| Recipe search in dialog | Load all recipes, filter client-side | MudAutocomplete with async SearchFunc | Scales to 1000+ recipes, server-side filtering |
| Date picker for "go to week" | Custom date input | MudDatePicker | Handles localization, touch targets, keyboard nav |
| Week day calculation | Manual arithmetic | DateOnly.AddDays() with DayOfWeek enum | Built-in, handles edge cases (year boundaries) |

**Key insight:** MudBlazor provides building blocks (Grid, Paper, Hidden, Dialog, Autocomplete) that compose well for custom layouts. Don't force a calendar library when simple CSS Grid suffices.

## Common Pitfalls

### Pitfall 1: MudHidden Renders Both Views During SSR

**What goes wrong:** On initial page load, both desktop and mobile views flash briefly before hiding.

**Why it happens:** MudHidden uses JavaScript to detect breakpoint. During SSR, breakpoint is unknown, so content renders, then hides after JS executes.

**How to avoid:**
1. Use `@rendermode InteractiveServer` on page (already default in this app)
2. Add loading skeleton that shows while initial breakpoint detection occurs
3. Or use CSS media queries directly for critical above-fold content

```razor
<style>
    @media (max-width: 959px) {
        .desktop-only { display: none; }
    }
    @media (min-width: 960px) {
        .mobile-only { display: none; }
    }
</style>
```

**Warning signs:** Layout jump on page load, content flashes.

### Pitfall 2: Recipe Picker Dialog Closes on Outside Click

**What goes wrong:** User accidentally clicks outside dialog, loses unsaved selection.

**Why it happens:** MudDialog default allows backdrop click to close.

**How to avoid:**
```csharp
var options = new DialogOptions
{
    CloseOnEscapeKey = true,
    BackdropClick = false, // Prevent accidental close
    MaxWidth = MaxWidth.Small,
    FullWidth = true
};

await DialogService.ShowAsync<RecipePickerDialog>("Select Meal", options);
```

**Warning signs:** User complaints about lost selections, support tickets.

### Pitfall 3: Week Boundaries When User Timezone Differs

**What goes wrong:** User in Pacific time sees "Monday" as Sunday's meals because server is UTC.

**Why it happens:** DateOnly.FromDateTime(DateTime.Today) uses server timezone. MealPlan stores DateOnly without timezone.

**How to avoid:** This app is single-household, family all in same timezone. For MVP, use server-local time consistently. If multi-timezone needed later, store user's timezone preference and calculate:

```csharp
// Current approach (OK for single-timezone household)
var today = DateOnly.FromDateTime(DateTime.Today);

// Future multi-timezone approach
var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId);
var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone);
var today = DateOnly.FromDateTime(userNow);
```

**Warning signs:** Meals appear on wrong day for some users.

### Pitfall 4: N+1 Query When Loading Recipes for Display

**What goes wrong:** Loading meal plan with 21 entries causes 21 separate recipe queries.

**Why it happens:** Lazy loading or forgetting `.Include()`.

**How to avoid:**
```csharp
// Always include Recipe navigation when loading entries
var mealPlan = await context.MealPlans
    .Include(mp => mp.Entries)
        .ThenInclude(e => e.Recipe)  // Eager load recipes
    .FirstOrDefaultAsync(mp => mp.HouseholdId == householdId
                            && mp.WeekStartDate == weekStartDate);
```

**Warning signs:** Slow page loads, many SQL queries in logs.

### Pitfall 5: Meal Slot State Not Updating After Dialog Close

**What goes wrong:** User adds meal via dialog, but slot still shows empty until page refresh.

**Why it happens:** Dialog returns result, but parent component doesn't refresh entries.

**How to avoid:**
```csharp
private async Task HandleSlotClick(DateOnly date, MealType mealType)
{
    var parameters = new DialogParameters<RecipePickerDialog>
    {
        { x => x.HouseholdId, _householdId },
        { x => x.Date, date },
        { x => x.MealType, mealType }
    };

    var dialog = await DialogService.ShowAsync<RecipePickerDialog>("Select Meal", parameters);
    var result = await dialog.Result;

    if (!result.Canceled)
    {
        // Reload entries after successful add
        await LoadMealPlanAsync();
        StateHasChanged();
    }
}
```

**Warning signs:** UI doesn't reflect recent changes, users must refresh.

### Pitfall 6: Deleting Recipe Breaks Meal Plan Display

**What goes wrong:** User deletes recipe that's in meal plan. Meal slot shows null or crashes.

**Why it happens:** Recipe soft-delete hides from query, but MealPlanEntry still references it. Global query filter excludes deleted recipe from Include.

**How to avoid:** MealPlanEntry configuration already has `OnDelete(DeleteBehavior.SetNull)` for RecipeId. When recipe is deleted:
1. EF Core sets RecipeId to null
2. Entry becomes a "custom meal" with original name? Or blank?

**Better approach:** Before soft-deleting recipe, update meal plan entries to store recipe name as CustomMealName:

```csharp
// In RecipeService.DeleteRecipeAsync
var affectedEntries = await context.MealPlanEntries
    .Where(e => e.HouseholdId == householdId && e.RecipeId == recipeId)
    .ToListAsync();

foreach (var entry in affectedEntries)
{
    entry.CustomMealName = recipe.Name + " (deleted)";
    entry.RecipeId = null;
}
```

**Warning signs:** Blank meal slots, null reference exceptions, lost meal history.

## Code Examples

Verified patterns from existing codebase and MudBlazor documentation:

### MealSlot Component

```razor
@* MealSlot.razor *@
<MudPaper Class="@GetSlotClass()"
          Elevation="@(Entry != null ? 2 : 0)"
          @onclick="HandleClick">
    @if (Entry != null)
    {
        @if (Entry.Recipe != null)
        {
            <div class="d-flex flex-column">
                @if (!string.IsNullOrWhiteSpace(Entry.Recipe.ImagePath))
                {
                    <MudImage Src="@Entry.Recipe.ImagePath"
                              Height="60"
                              ObjectFit="ObjectFit.Cover"
                              Class="rounded-t" />
                }
                <MudText Typo="Typo.body2" Class="pa-1 text-truncate">
                    @Entry.Recipe.Name
                </MudText>
            </div>
        }
        else if (!string.IsNullOrWhiteSpace(Entry.CustomMealName))
        {
            <MudText Typo="Typo.body2" Class="pa-2 font-italic" Color="Color.Secondary">
                @Entry.CustomMealName
            </MudText>
        }
        <MudIconButton Icon="@Icons.Material.Filled.Close"
                       Size="Size.Small"
                       Class="slot-remove"
                       OnClick="HandleRemove"
                       OnClick:stopPropagation="true" />
    }
    else
    {
        <div class="slot-empty">
            <MudIcon Icon="@Icons.Material.Filled.Add" Size="Size.Small" Color="Color.Secondary" />
        </div>
    }
</MudPaper>

@code {
    [Parameter] public DateOnly Date { get; set; }
    [Parameter] public MealType MealType { get; set; }
    [Parameter] public MealPlanEntry? Entry { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
    [Parameter] public EventCallback OnRemove { get; set; }

    private string GetSlotClass()
    {
        var baseClass = "meal-slot";
        if (Entry == null) baseClass += " meal-slot-empty";
        return baseClass;
    }

    private async Task HandleClick() => await OnClick.InvokeAsync();
    private async Task HandleRemove() => await OnRemove.InvokeAsync();
}

<style>
    .meal-slot {
        min-height: 80px;
        cursor: pointer;
        position: relative;
        transition: background-color 0.2s;
    }

    .meal-slot:hover {
        background-color: var(--mud-palette-action-default-hover);
    }

    .meal-slot-empty {
        border: 2px dashed var(--mud-palette-lines-default);
        display: flex;
        align-items: center;
        justify-content: center;
    }

    .slot-empty {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100%;
    }

    .slot-remove {
        position: absolute;
        top: 2px;
        right: 2px;
        opacity: 0;
    }

    .meal-slot:hover .slot-remove {
        opacity: 1;
    }
</style>
```

### MealPlanService Implementation

```csharp
// MealPlanService.cs
public interface IMealPlanService
{
    Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken ct = default);
    Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, CancellationToken ct = default);
    Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken ct = default);
    DateOnly GetWeekStartDate(DateOnly date);
    DateOnly[] GetWeekDays(DateOnly weekStart);
}

public class MealPlanService : IMealPlanService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<MealPlanService> _logger;

    public MealPlanService(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<MealPlanService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public DateOnly GetWeekStartDate(DateOnly date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff);
    }

    public DateOnly[] GetWeekDays(DateOnly weekStart)
    {
        return Enumerable.Range(0, 7)
            .Select(i => weekStart.AddDays(i))
            .ToArray();
    }

    public async Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var mealPlan = await context.MealPlans
            .Include(mp => mp.Entries)
                .ThenInclude(e => e.Recipe)
            .FirstOrDefaultAsync(mp => mp.HouseholdId == householdId
                                    && mp.WeekStartDate == weekStart, ct);

        if (mealPlan == null)
        {
            var maxId = await context.MealPlans
                .Where(mp => mp.HouseholdId == householdId)
                .MaxAsync(mp => (int?)mp.MealPlanId, ct) ?? 0;

            mealPlan = new MealPlan
            {
                HouseholdId = householdId,
                MealPlanId = maxId + 1,
                WeekStartDate = weekStart,
                CreatedAt = DateTime.UtcNow,
                Entries = new List<MealPlanEntry>()
            };
            context.MealPlans.Add(mealPlan);
            await context.SaveChangesAsync(ct);
            _logger.LogInformation("Created MealPlan {MealPlanId} for household {HouseholdId}, week {WeekStart}",
                mealPlan.MealPlanId, householdId, weekStart);
        }

        return mealPlan;
    }

    public async Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var weekStart = GetWeekStartDate(date);
        var mealPlan = await GetOrCreateMealPlanAsync(householdId, weekStart, ct);

        // Check for existing entry at this slot
        var existing = await context.MealPlanEntries
            .FirstOrDefaultAsync(e => e.HouseholdId == householdId
                                   && e.MealPlanId == mealPlan.MealPlanId
                                   && e.Date == date
                                   && e.MealType == mealType, ct);

        if (existing != null)
        {
            // Update existing
            existing.RecipeId = recipeId;
            existing.CustomMealName = customMealName;
        }
        else
        {
            var maxEntryId = await context.MealPlanEntries
                .Where(e => e.HouseholdId == householdId && e.MealPlanId == mealPlan.MealPlanId)
                .MaxAsync(e => (int?)e.EntryId, ct) ?? 0;

            existing = new MealPlanEntry
            {
                HouseholdId = householdId,
                MealPlanId = mealPlan.MealPlanId,
                EntryId = maxEntryId + 1,
                Date = date,
                MealType = mealType,
                RecipeId = recipeId,
                CustomMealName = customMealName
            };
            context.MealPlanEntries.Add(existing);
        }

        await context.SaveChangesAsync(ct);
        _logger.LogInformation("Added meal to {Date} {MealType}: RecipeId={RecipeId}, Custom={CustomMealName}",
            date, mealType, recipeId, customMealName);

        return existing;
    }

    public async Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var entry = await context.MealPlanEntries
            .FirstOrDefaultAsync(e => e.HouseholdId == householdId
                                   && e.MealPlanId == mealPlanId
                                   && e.EntryId == entryId, ct);

        if (entry != null)
        {
            context.MealPlanEntries.Remove(entry);
            await context.SaveChangesAsync(ct);
            _logger.LogInformation("Removed meal entry {EntryId} from MealPlan {MealPlanId}", entryId, mealPlanId);
        }
    }
}
```

### Week Navigation Component

```razor
@* MealPlanNavigation.razor *@
<div class="d-flex justify-space-between align-center mb-4">
    <MudIconButton Icon="@Icons.Material.Filled.ChevronLeft"
                   OnClick="PreviousWeek"
                   aria-label="Previous week" />

    <div class="d-flex flex-column align-center">
        <MudText Typo="Typo.h5">
            @WeekStartDate.ToString("MMM d") - @WeekStartDate.AddDays(6).ToString("MMM d, yyyy")
        </MudText>
        @if (IsCurrentWeek)
        {
            <MudChip T="string" Size="Size.Small" Color="Color.Primary">This Week</MudChip>
        }
    </div>

    <MudIconButton Icon="@Icons.Material.Filled.ChevronRight"
                   OnClick="NextWeek"
                   aria-label="Next week" />
</div>

<div class="d-flex justify-center mb-4">
    <MudButton StartIcon="@Icons.Material.Filled.Today"
               Variant="Variant.Text"
               OnClick="GoToToday"
               Disabled="@IsCurrentWeek">
        Today
    </MudButton>
</div>

@code {
    [Parameter] public DateOnly WeekStartDate { get; set; }
    [Parameter] public EventCallback<DateOnly> WeekStartDateChanged { get; set; }

    private bool IsCurrentWeek
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var currentWeekStart = GetWeekStart(today);
            return WeekStartDate == currentWeekStart;
        }
    }

    private DateOnly GetWeekStart(DateOnly date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff);
    }

    private async Task PreviousWeek() => await WeekStartDateChanged.InvokeAsync(WeekStartDate.AddDays(-7));
    private async Task NextWeek() => await WeekStartDateChanged.InvokeAsync(WeekStartDate.AddDays(7));

    private async Task GoToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = GetWeekStart(today);
        await WeekStartDateChanged.InvokeAsync(weekStart);
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Modal dialogs for all interactions | Dialog for selection, inline for display | 2022+ | Dialogs for focused input, inline for browsing |
| Full-page calendar components | Custom CSS Grid for simple schedules | 2023+ | Simpler code, better performance, tailored UX |
| Client-side routing for every action | Dialog + reload pattern | Blazor Server | Maintains context, fewer round trips |
| Separate mobile app/site | Responsive with MudHidden/media queries | 2020+ | Single codebase, consistent data |

**Deprecated/outdated:**
- **Using external calendar libraries for simple grids**: Overkill, adds bundle size, harder to customize
- **Building separate mobile components**: Use responsive design instead
- **JavaScript-heavy date pickers**: MudDatePicker handles this natively in Blazor

## Open Questions

Things that couldn't be fully resolved:

1. **Snack MealType**
   - What we know: MealPlanEntry.MealType enum includes `Snack`. Requirements only mention Breakfast/Lunch/Dinner.
   - What's unclear: Should Snack be included in UI? Would add fourth row to grid.
   - Recommendation: Include Snack in implementation but hide from default view. Add setting to show/hide Snack row later if users request it. Data model supports it.

2. **Mobile List View Grouping**
   - What we know: Requirement says "list format on mobile" but doesn't specify grouping.
   - What's unclear: Group by day (Mon: B/L/D, Tue: B/L/D) or by meal type (All breakfasts, all lunches)?
   - Recommendation: Group by day (more intuitive for daily planning). Each day is expandable accordion or stacked cards.

3. **Empty Week Display**
   - What we know: New week has no entries.
   - What's unclear: Show empty grid or prompt to add meals?
   - Recommendation: Show empty grid with clickable slots. First-time users need obvious affordance - dashed borders on empty slots with + icon.

4. **Recipe Quick Preview in Meal Slot**
   - What we know: Requirement says "click meal in plan to view full recipe details".
   - What's unclear: Navigate to recipe page OR show recipe in dialog/expansion?
   - Recommendation: Show recipe in dialog (maintains meal plan context). Use same RecipeCard expand pattern from Phase 2 inside dialog. Navigation to edit would still go to RecipeEdit page.

## Sources

### Primary (HIGH confidence)

- MudBlazor Hidden component documentation: https://mudblazor.com/components/hidden
- MudBlazor Dialog documentation: https://mudblazor.com/components/dialog
- MudBlazor Select/Autocomplete documentation: https://mudblazor.com/components/select
- Existing codebase patterns: RecipeService, RecipeCard, Recipes.razor

### Secondary (MEDIUM confidence)

- CSS Grid calendar tutorial: https://zellwk.com/blog/calendar-with-css-grid/
- Heron.MudCalendar GitHub: https://github.com/danheron/Heron.MudCalendar
- EF Core DateOnly support: https://erikej.github.io/efcore/sqlserver/2023/09/03/efcore-dateonly-timeonly.html
- Meal planning UX case studies: https://stevenwett.com/work/supper-meal-planning

### Tertiary (LOW confidence - requires validation)

- MudHidden SSR behavior with breakpoints (may need testing)
- Recipe deletion cascade to meal plan entries (needs testing with SetNull behavior)

## Metadata

**Confidence breakdown:**
- Standard stack (MudBlazor components): HIGH - Already in use, documented, verified in Phase 2
- Architecture patterns (custom grid, dialog picker): MEDIUM - Based on established patterns but not yet implemented
- Service layer (MealPlanService): HIGH - Follows existing RecipeService patterns exactly
- Responsive switching: MEDIUM - MudHidden documented but SSR edge cases may exist
- Week calculation: HIGH - DateOnly arithmetic is straightforward

**Research date:** 2026-01-23
**Valid until:** ~60 days (stable ecosystem, patterns well-established)
**Re-research needed if:**
- Complex calendar features requested (month view, drag-and-drop)
- Multi-timezone support required
- Performance issues with grid rendering on low-end mobile
