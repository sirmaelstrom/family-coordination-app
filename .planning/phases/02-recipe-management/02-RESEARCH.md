# Phase 02: Recipe Management - Research

**Researched:** 2026-01-23
**Domain:** Blazor Server recipe management with structured ingredients
**Confidence:** MEDIUM

## Summary

Research focused on implementing recipe CRUD operations in Blazor Server with the specific UI/UX decisions from CONTEXT.md. The standard stack centers on **MudBlazor** for UI components (cards, forms, autocomplete, dark mode), **blazor-dragdrop** for ingredient reordering, and **Markdig** for markdown rendering. Image handling uses Blazor's built-in `IBrowserFile` with filesystem storage (not database). Ingredient parsing will require a custom C# solution (no suitable NuGet packages found), leveraging `Fractions` library for quantity display.

Key finding: Blazor Server has specific **SignalR limitations** with file uploads (32 KB default message size, memory pressure from chunks over WebSocket). The recommendation is to stream files directly to filesystem, enforce 10 MB client-side limit, and increase SignalR `MaximumReceiveMessageSize` to ~12 MB.

**Primary recommendation:** Use MudBlazor for UI, implement custom ingredient parser with Fractions library, store images on filesystem with database paths, and auto-save drafts using debounced events with EditContext.IsModified() for dirty tracking.

## Standard Stack

The established libraries/tools for Blazor Server recipe management:

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MudBlazor | 7.x+ | UI component library | Material Design components, excellent dark mode support, 23M+ downloads, active maintenance, comprehensive form/validation/autocomplete components |
| Markdig | 0.37.0+ | Markdown rendering | Official .NET Foundation project, fastest C# markdown parser, widely used in Blazor apps |
| Fractions | 8.3.2 | Fraction display/storage | Supports BigInteger numerator/denominator, operator overloads, no dependencies, handles mixed fractions and Unicode (½, ¼) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.x | PostgreSQL EF Core provider | Official provider for PostgreSQL, maps byte[] to bytea for images |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| blazor-dragdrop | 2.6.1 | Drag and drop for ingredient reordering | HTML5 drag-and-drop wrapper, works on desktop, requires mobile-drag-drop polyfill for mobile |
| SixLabors.ImageSharp | 3.1.12+ | Image thumbnail generation | Optional - if implementing server-side thumbnails (Claude's discretion). Cross-platform, .NET 6+, Lanczos3 for high quality |
| EfCore.SoftDeleteServices | 1.3.0 | Soft delete with cascade | Optional - if cascading soft delete needed. Simplifies cascade behavior vs manual implementation |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| MudBlazor | Radzen Blazor Components | Radzen has more data-grid focus (70+ components vs 50+), heavier for enterprise CRUD. MudBlazor is lighter, better Material Design aesthetic, stronger community (10k stars vs 2k) |
| MudBlazor | Blazorise | More dependencies (Bootstrap/Tailwind), less cohesive than MudBlazor's built-in theming |
| Custom ingredient parser | Port Python ingredient-parser | Python library (strangetom/ingredient-parser) is well-tested but would require porting. No C# equivalent found on NuGet |
| Filesystem storage | Azure Blob Storage | Cloud storage better for production scale. Filesystem simpler for single-server MVP, can migrate later |
| Filesystem storage | Database (bytea) | Avoid - PostgreSQL bytea works but database bloat, memory pressure on large images, harder to serve via CDN later |

**Installation:**
```bash
dotnet add package MudBlazor
dotnet add package Markdig
dotnet add package Fractions
dotnet add package blazor-dragdrop
# Optional
dotnet add package SixLabors.ImageSharp
```

**Blazor-dragdrop setup (Program.cs):**
```csharp
builder.Services.AddBlazorDragDrop();
```

**MudBlazor setup (Program.cs):**
```csharp
builder.Services.AddMudServices();
```

## Architecture Patterns

### Recommended Project Structure

```
Components/
├── Pages/
│   ├── Recipes.razor              # Recipe list (cards, search, empty state)
│   └── RecipeEdit.razor           # Recipe create/edit form
├── Components/
│   ├── RecipeCard.razor           # Individual recipe card with expand
│   ├── IngredientEntry.razor      # Ingredient input with parsing
│   ├── IngredientList.razor       # Drag-drop ingredient list
│   └── MarkdownPreview.razor      # Markdown instructions display
├── Services/
│   ├── RecipeService.cs           # Recipe CRUD operations
│   ├── IngredientParser.cs        # Parse "2 cups flour" → structured
│   ├── ImageService.cs            # File upload, storage, retrieval
│   └── DraftService.cs            # Auto-save draft persistence
Models/
├── Recipe.cs                      # EF Core entity
├── Ingredient.cs                  # EF Core entity with composite key
├── Category.cs                    # EF Core entity (soft delete)
└── RecipeIngredient.cs            # Join entity with order
```

### Pattern 1: Card-Based List with In-Place Expansion

**What:** Recipe list renders `MudCard` components in a grid. Clicking card expands it within the same layout (not modal, not navigation).

**When to use:** When users need to quickly browse multiple recipes without losing context. Avoids navigation overhead.

**Example:**
```csharp
@* Recipes.razor *@
@foreach (var recipe in recipes)
{
    <MudCard @onclick="() => ToggleExpanded(recipe.Id)">
        <MudCardMedia Image="@recipe.ImageUrl" Height="200" />
        <MudCardContent>
            <MudText Typo="Typo.h6">@recipe.Name</MudText>
            <MudText Typo="Typo.body2">@GetIngredientPreview(recipe)</MudText>
        </MudCardContent>
        @if (expandedId == recipe.Id)
        {
            <MudCardActions>
                <MudButton OnClick="() => EditRecipe(recipe.Id)">Edit</MudButton>
                <MudButton OnClick="() => DeleteRecipe(recipe.Id)">Delete</MudButton>
                <MudButton>Add to Meal Plan</MudButton>
            </MudCardActions>
            <MudCardContent>
                <MudText>@recipe.Description</MudText>
                <!-- Full ingredient list, instructions markdown -->
            </MudCardContent>
        }
    </MudCard>
}
```

### Pattern 2: Hybrid Ingredient Entry with Parsing

**What:** Single text input accepts free-form text ("2 1/2 cups flour"), parses on blur/enter into structured fields (quantity, unit, name). Shows parsed result in editable fields below.

**When to use:** When users have varied input sources (typed, pasted from websites) and need both speed and accuracy.

**Example:**
```csharp
@* IngredientEntry.razor *@
<MudTextField @bind-Value="rawInput"
              Label="Add ingredient"
              OnBlur="ParseIngredient"
              OnKeyDown="HandleKeyDown" />

@if (parsedIngredient != null)
{
    <MudGrid>
        <MudItem xs="3">
            <MudTextField @bind-Value="parsedIngredient.Quantity" Label="Quantity" />
        </MudItem>
        <MudItem xs="3">
            <MudAutocomplete @bind-Value="parsedIngredient.Unit"
                            SearchFunc="SearchUnits"
                            Label="Unit" />
        </MudItem>
        <MudItem xs="4">
            <MudAutocomplete @bind-Value="parsedIngredient.Name"
                            SearchFunc="SearchIngredients"
                            Label="Ingredient" />
        </MudItem>
        <MudItem xs="2">
            <MudSelect @bind-Value="parsedIngredient.CategoryId" Label="Category">
                @foreach (var cat in categories)
                {
                    <MudSelectItem Value="@cat.Id">@cat.Name</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
    </MudGrid>
    <MudTextField @bind-Value="parsedIngredient.Notes"
                  Label="Notes (optional)"
                  Lines="2" />
}
```

### Pattern 3: Auto-Save with Debounced EditContext

**What:** Form auto-saves on input change after debounce delay (e.g., 2 seconds of no typing). Uses `EditContext.IsModified()` to detect dirty state and NavigationLock to prevent navigation loss.

**When to use:** Long forms where users may navigate away accidentally. Reduces data loss frustration.

**Example:**
```csharp
@* RecipeEdit.razor *@
@inject DraftService DraftService
@implements IDisposable

<EditForm EditContext="editContext" OnValidSubmit="SaveRecipe">
    <NavigationLock ConfirmExternalNavigation="@editContext.IsModified()" />

    <MudTextField @bind-Value="recipe.Name"
                  Label="Recipe Name"
                  @oninput="ScheduleAutoSave" />
    <!-- More fields -->
</EditForm>

@code {
    private System.Threading.Timer? autoSaveTimer;
    private const int AutoSaveDelayMs = 2000;

    private void ScheduleAutoSave()
    {
        autoSaveTimer?.Dispose();
        autoSaveTimer = new System.Threading.Timer(async _ =>
        {
            if (editContext.IsModified())
            {
                await InvokeAsync(async () =>
                {
                    await DraftService.SaveDraftAsync(recipe);
                    editContext.MarkAsUnmodified();
                    await InvokeAsync(StateHasChanged);
                });
            }
        }, null, AutoSaveDelayMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        autoSaveTimer?.Dispose();
    }
}
```

### Pattern 4: Drag-and-Drop Ingredient Reordering

**What:** Use `blazor-dragdrop` to allow users to reorder ingredients and create groups by dragging into named sections.

**When to use:** When order matters (recipe instructions reference ingredient order) and users need visual organization.

**Example:**
```csharp
@using Plk.Blazor.DragDrop

<Dropzone Items="ingredients" InstantReplace="true" TItem="Ingredient">
    <ChildContent>
        @foreach (var ingredient in context)
        {
            <MudPaper Class="pa-2 ma-1" Elevation="2">
                <MudText>@FormatIngredient(ingredient)</MudText>
                <MudIconButton Icon="@Icons.Material.Filled.Delete"
                              Size="Size.Small"
                              OnClick="() => DeleteWithUndo(ingredient)" />
            </MudPaper>
        }
    </ChildContent>
</Dropzone>

@* Ingredient groups *@
@foreach (var group in ingredientGroups)
{
    <MudText Typo="Typo.subtitle1" Class="mt-4">@group.Name</MudText>
    <Dropzone Items="group.Ingredients" InstantReplace="true" TItem="Ingredient">
        <!-- Same as above -->
    </Dropzone>
}
```

### Pattern 5: Soft Delete with EF Core Global Query Filter

**What:** Add `IsDeleted` bool to entities, override `SaveChanges` to intercept deletions, apply global query filter to exclude deleted records automatically.

**When to use:** When users need "undo delete" functionality and data recovery without complex versioning.

**Example:**
```csharp
// Recipe.cs
public class Recipe
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public string Name { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

// ApplicationDbContext.cs
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Recipe>()
        .HasQueryFilter(r => !r.IsDeleted);

    modelBuilder.Entity<Category>()
        .HasQueryFilter(c => !c.IsDeleted);
}

public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    foreach (var entry in ChangeTracker.Entries<Recipe>())
    {
        if (entry.State == EntityState.Deleted)
        {
            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            entry.Entity.DeletedAt = DateTime.UtcNow;
        }
    }

    return base.SaveChangesAsync(cancellationToken);
}
```

### Pattern 6: File Upload with Stream-to-Filesystem

**What:** Stream uploaded files directly to filesystem without loading into memory. Store filename in database, serve via static file middleware or controller action.

**When to use:** Blazor Server with file uploads (avoids SignalR message size/memory issues).

**Example:**
```csharp
// ImageService.cs
public async Task<string> SaveImageAsync(IBrowserFile file, Guid householdId)
{
    var trustedFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.Name)}";
    var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads", householdId.ToString());
    Directory.CreateDirectory(uploadsPath);

    var filePath = Path.Combine(uploadsPath, trustedFileName);

    const long maxFileSize = 10 * 1024 * 1024; // 10 MB
    await using FileStream fs = new(filePath, FileMode.Create);
    await file.OpenReadStream(maxFileSize).CopyToAsync(fs);

    return $"/uploads/{householdId}/{trustedFileName}";
}
```

### Anti-Patterns to Avoid

- **Loading images into MemoryStream in Blazor Server:** Causes memory pressure, GC issues, and SignalR message size exceptions. Always stream directly to filesystem/blob storage.
- **Storing images in database (bytea):** Database bloat, difficult to serve via CDN, no benefit over filesystem for single-server deployment. Use filesystem with database storing path.
- **Using multiple `ValidationMessage` components instead of `ValidationSummary`:** Clutters UI. Use `ValidationSummary` at form top for overview, `ValidationMessage` only for critical inline feedback.
- **Implementing ingredient parsing with complex regex chains:** Fragile, hard to maintain, poor error messages. Use tokenization/parser combinator approach (see Sprache/Pidgin) or rule-based state machine.
- **Not setting `maxAllowedSize` on `OpenReadStream()`:** Client can spoof `IBrowserFile.Size`. Always set explicit server-side limit.
- **Cascading delete instead of cascade soft delete:** Deleting recipe cascades to ingredients (expected), but deleting category shouldn't cascade-delete recipes. Prompt user to reassign or block delete.

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Markdown rendering | Custom parser with regex | Markdig | Markdown spec has 100+ edge cases (escaping, nested lists, code blocks, HTML). Markdig is 10+ years mature, handles CommonMark spec |
| Fraction display/parsing | String manipulation (1/2 → 0.5) | Fractions library | Handles mixed fractions (1 1/2), Unicode (½), arithmetic, conversion to decimal, BigInteger for precision. Hand-rolled breaks on edge cases |
| Dark mode theming | Manual CSS classes | MudBlazor theme system | MudBlazor has built-in palette, CSS variables, theme provider. Hand-rolling means maintaining duplicate styles for every component |
| File upload validation | Client-side checks only | IBrowserFile with server-side maxAllowedSize | Client can spoof size/type. Server-side enforcement is mandatory for security |
| Autocomplete/typeahead | Manual filtering with debounce | MudAutocomplete | Handles keyboard nav, async search, loading states, empty states, templates. Hand-rolled misses accessibility (ARIA) |
| Soft delete | Manual deletion flag without query filter | EF Core global query filter or EfCore.SoftDeleteServices | Query filter applies automatically to all queries. Manual filtering is error-prone (forget one WHERE clause = show deleted data) |

**Key insight:** Blazor Server's SignalR foundation creates unique constraints (message size, connection-based state). Use libraries that account for these (MudBlazor does), not generic JS libraries ported to Blazor.

## Common Pitfalls

### Pitfall 1: SignalR Message Size Exceeded on File Upload

**What goes wrong:** Uploading files >32 KB (default SignalR limit) throws exception `Error: Server returned an error on close: Connection closed with an error. InvalidDataException: The maximum message size of 32768B was exceeded.`

**Why it happens:** Blazor Server sends file chunks over SignalR WebSocket. Default `MaximumReceiveMessageSize` is 32 KB. File uploads chunk data and send via SignalR, hitting limit quickly.

**How to avoid:**
1. Increase SignalR message size in `Program.cs`:
   ```csharp
   builder.Services.AddServerSideBlazor()
       .AddHubOptions(options =>
       {
           options.MaximumReceiveMessageSize = 12 * 1024 * 1024; // 12 MB (10 MB file + overhead)
       });
   ```
2. Enforce client-side limit to stay under server limit:
   ```csharp
   const long maxFileSize = 10 * 1024 * 1024; // 10 MB
   await file.OpenReadStream(maxFileSize).CopyToAsync(fs);
   ```
3. Stream directly to filesystem, never to MemoryStream.

**Warning signs:** "Connection closed with an error" when uploading images, browser console shows `InvalidDataException` about message size.

### Pitfall 2: Memory Pressure from Loading Files into Memory

**What goes wrong:** Application memory usage spikes, GC runs frequently, server becomes slow or crashes under concurrent uploads.

**Why it happens:** Loading file streams into `MemoryStream` or `byte[]` before saving allocates large objects on heap. Blazor Server is stateful (one circuit per user), so multiple users uploading = memory adds up. GC struggles with large object heap (LOH).

**How to avoid:**
```csharp
// ❌ WRONG - loads entire file into memory
using var memoryStream = new MemoryStream();
await file.OpenReadStream(maxSize).CopyToAsync(memoryStream);
byte[] imageBytes = memoryStream.ToArray(); // LOH allocation
await _dbContext.Recipes.Add(new Recipe { ImageData = imageBytes });

// ✅ CORRECT - stream directly to filesystem
await using FileStream fs = new(filePath, FileMode.Create);
await file.OpenReadStream(maxSize).CopyToAsync(fs);
// Store only path in database
await _dbContext.Recipes.Add(new Recipe { ImagePath = filePath });
```

**Warning signs:** Server memory usage grows with uploads, doesn't drop after upload completes, `OutOfMemoryException` under load.

### Pitfall 3: Ingredient Autocomplete Performance with Large Datasets

**What goes wrong:** Autocomplete search becomes slow as household adds hundreds of unique ingredients. UI freezes during typing.

**Why it happens:** `SearchFunc` in MudAutocomplete queries database on every keystroke without debouncing or optimization. EF Core query translates to `LIKE '%search%'` full table scan.

**How to avoid:**
1. Use MudAutocomplete's `DebounceInterval` (default 100ms, increase to 300ms for database queries):
   ```csharp
   <MudAutocomplete DebounceInterval="300" SearchFunc="SearchIngredients" />
   ```
2. Index ingredient name column in database:
   ```csharp
   modelBuilder.Entity<Ingredient>()
       .HasIndex(i => i.Name);
   ```
3. Limit search results (top 10-20):
   ```csharp
   private async Task<IEnumerable<string>> SearchIngredients(string value)
   {
       if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

       return await _dbContext.Ingredients
           .Where(i => i.HouseholdId == currentHouseholdId
                    && i.Name.StartsWith(value)) // StartsWith uses index
           .Select(i => i.Name)
           .Distinct()
           .Take(20)
           .ToListAsync();
   }
   ```
4. Use `MinCharacters="2"` to avoid searching on single character.

**Warning signs:** UI lag when typing in ingredient field, database CPU spikes during autocomplete, slow query logs show full table scans.

### Pitfall 4: Drag-and-Drop Not Working on Mobile

**What goes wrong:** Ingredient reordering works on desktop but tapping/dragging does nothing on mobile.

**Why it happens:** HTML5 drag-and-drop API is not supported by most mobile browsers. `blazor-dragdrop` uses HTML5 API, which is desktop-only.

**How to avoid:**
1. Add mobile polyfill library `mobile-drag-drop` (see blazor-dragdrop docs):
   ```html
   <!-- In _Host.cshtml or App.razor -->
   <script src="https://cdn.jsdelivr.net/npm/mobile-drag-drop@2.3.0/index.min.js"></script>
   <script>
       MobileDragDrop.polyfill({
           dragImageTranslateOverride: MobileDragDrop.scrollBehaviourDragImageTranslateOverride
       });
   </script>
   ```
2. Test on actual mobile device (Chrome DevTools mobile emulation doesn't emulate touch events perfectly).

**Warning signs:** Drag works in desktop browser, fails in mobile browser, no console errors.

### Pitfall 5: Markdown Injection (XSS) in Recipe Instructions

**What goes wrong:** User inputs `<script>alert('XSS')</script>` in recipe instructions. Markdig renders it as HTML, executes script in other users' browsers.

**Why it happens:** Markdig converts markdown to HTML. If rendered with `@((MarkupString)html)`, HTML executes as-is. Markdown allows raw HTML by default.

**How to avoid:**
1. Disable raw HTML in Markdig pipeline:
   ```csharp
   var pipeline = new MarkdownPipelineBuilder()
       .DisableHtml() // ← Critical for user-generated content
       .UseAdvancedExtensions()
       .Build();
   var html = Markdown.ToHtml(markdownText, pipeline);
   ```
2. Sanitize output with HTML sanitizer if raw HTML is needed:
   ```csharp
   dotnet add package HtmlSanitizer

   var sanitizer = new HtmlSanitizer();
   var safeHtml = sanitizer.Sanitize(html);
   ```

**Warning signs:** Script tags visible in rendered output, unexpected JavaScript execution, security audit flags XSS vulnerability.

### Pitfall 6: EditContext.IsModified() Doesn't Detect Programmatic Changes

**What goes wrong:** Auto-save doesn't trigger when ingredient list changes (add/remove), only when text fields change.

**Why it happens:** `EditContext.IsModified()` tracks field changes via bindings. Programmatic changes to model properties (e.g., `recipe.Ingredients.Add(...)`) don't mark EditContext as modified.

**How to avoid:**
```csharp
private void AddIngredient(Ingredient ingredient)
{
    recipe.Ingredients.Add(ingredient);
    editContext.NotifyFieldChanged(editContext.Field(nameof(recipe.Ingredients)));
    ScheduleAutoSave(); // Now IsModified() returns true
}
```

**Warning signs:** Form doesn't auto-save after adding ingredients, NavigationLock doesn't trigger on ingredient changes.

### Pitfall 7: Soft Delete Query Filter Breaks Explicit .Include()

**What goes wrong:** Query with `.Include(r => r.Category)` returns null Category even though category exists (it's soft-deleted).

**Why it happens:** Global query filter applies to navigation properties. Deleted category is filtered out of include.

**How to avoid:**
1. Design: Don't allow deleting categories that are referenced by recipes. Block delete, prompt user to reassign.
2. If must support: Use `.IgnoreQueryFilters()` when explicitly loading soft-deleted relations:
   ```csharp
   var recipe = await _dbContext.Recipes
       .Include(r => r.Category)
       .IgnoreQueryFilters() // Shows soft-deleted categories
       .FirstOrDefaultAsync(r => r.Id == id);

   // Then filter manually if needed
   if (recipe.Category?.IsDeleted == true)
   {
       recipe.Category = null; // Or load default
   }
   ```

**Warning signs:** Navigation properties unexpectedly null, queries work without Include but not with Include.

## Code Examples

Verified patterns from official sources and established libraries:

### MudBlazor Card Grid with Dark Mode

```razor
@* Source: https://mudblazor.com/components/card *@
@* Dark mode via MudThemeProvider in App.razor or MainLayout.razor *@

<MudThemeProvider @bind-IsDarkMode="@_isDarkMode" Theme="_theme" />

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-4">
    <MudGrid>
        @foreach (var recipe in recipes)
        {
            <MudItem xs="12" sm="6" md="4">
                <MudCard Elevation="2">
                    <MudCardMedia Image="@recipe.ImageUrl" Height="200" />
                    <MudCardContent>
                        <MudText Typo="Typo.h6">@recipe.Name</MudText>
                        <MudText Typo="Typo.body2" Color="Color.Secondary">
                            @GetIngredientPreview(recipe)
                        </MudText>
                    </MudCardContent>
                </MudCard>
            </MudItem>
        }
    </MudGrid>
</MudContainer>

@code {
    private bool _isDarkMode = true; // Dark mode first

    private MudTheme _theme = new()
    {
        Palette = new PaletteLight() // Override for custom colors
        {
            // Soft dark gray background (not true black)
            Background = "#1e1e1e",
            Surface = "#2d2d2d",
            Primary = "#bb86fc",
            // ... more customization
        }
    };
}
```

### File Upload with IBrowserFile (Streaming to Filesystem)

```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/blazor/file-uploads

@page "/recipe/edit"
@inject IWebHostEnvironment Environment

<InputFile OnChange="LoadImage" accept="image/*;capture=camera" />
@* accept="image/*;capture=camera" enables camera on mobile *@

@code {
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    private async Task LoadImage(InputFileChangeEventArgs e)
    {
        var file = e.File;

        try
        {
            var trustedFileName = Path.GetRandomFileName();
            var path = Path.Combine(
                Environment.WebRootPath,
                "uploads",
                currentHouseholdId.ToString(),
                trustedFileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using FileStream fs = new(path, FileMode.Create);
            await file.OpenReadStream(MaxFileSize).CopyToAsync(fs);

            recipe.ImagePath = $"/uploads/{currentHouseholdId}/{trustedFileName}";
        }
        catch (IOException ex) when (ex.Message.Contains("maximum message size"))
        {
            // SignalR message size exceeded
            errorMessage = "File too large. Maximum size is 10 MB.";
        }
    }
}
```

### Fraction Display with Fractions Library

```csharp
// Source: https://www.nuget.org/packages/Fractions/
using Fractions;

// Parsing user input
var fraction = Fraction.FromString("2 1/2"); // Mixed fraction
// Result: 5/2

// Display as fraction
Console.WriteLine(fraction.ToString()); // "5/2"
Console.WriteLine(fraction.ToMixedNumberString()); // "2 1/2"

// Arithmetic
var doubled = fraction * 2; // 5/1 (or 5)

// Storage
public class Ingredient
{
    public string QuantityNumerator { get; set; } // Store as strings for BigInteger
    public string QuantityDenominator { get; set; }

    [NotMapped]
    public Fraction Quantity
    {
        get => new Fraction(
            BigInteger.Parse(QuantityNumerator),
            BigInteger.Parse(QuantityDenominator));
        set
        {
            QuantityNumerator = value.Numerator.ToString();
            QuantityDenominator = value.Denominator.ToString();
        }
    }
}
```

### Markdown Rendering with Markdig

```razor
@* Source: https://github.com/xoofx/markdig *@
@using Markdig
@inject ILogger<RecipeView> Logger

<MudPaper Class="pa-4">
    <MudText Typo="Typo.h6">Instructions</MudText>
    @((MarkupString)GetMarkdownHtml(recipe.Instructions))
</MudPaper>

@code {
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml() // Security: prevent XSS from user markdown
        .UseAdvancedExtensions() // Tables, task lists, etc.
        .Build();

    private string GetMarkdownHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        try
        {
            return Markdown.ToHtml(markdown, Pipeline);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to render markdown");
            return $"<pre>{WebUtility.HtmlEncode(markdown)}</pre>";
        }
    }
}
```

### MudAutocomplete for Ingredient Names

```razor
@* Source: https://mudblazor.com/components/autocomplete *@

<MudAutocomplete T="string"
                 Label="Ingredient"
                 @bind-Value="ingredient.Name"
                 SearchFunc="SearchIngredients"
                 DebounceInterval="300"
                 MinCharacters="2"
                 ResetValueOnEmptyText="false"
                 CoerceText="true">
</MudAutocomplete>

@code {
    private async Task<IEnumerable<string>> SearchIngredients(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        await using var context = await DbContextFactory.CreateDbContextAsync();

        return await context.Ingredients
            .Where(i => i.HouseholdId == currentHouseholdId
                     && i.Name.StartsWith(value))
            .Select(i => i.Name)
            .Distinct()
            .Take(20)
            .ToListAsync();
    }
}
```

### Debounced Auto-Save with EditContext

```razor
@* Pattern from: https://blog.jeremylikness.com/blog/an-easier-blazor-debounce/ *@
@implements IDisposable

<EditForm EditContext="editContext" OnValidSubmit="SaveRecipe">
    <DataAnnotationsValidator />
    <NavigationLock ConfirmExternalNavigation="@editContext.IsModified()" />

    <MudTextField @bind-Value="recipe.Name"
                  Label="Recipe Name"
                  @oninput="ScheduleAutoSave" />

    @if (autoSaveStatus == "Saving...")
    {
        <MudProgressCircular Size="Size.Small" Indeterminate="true" />
    }
    else if (autoSaveStatus == "Saved")
    {
        <MudText Typo="Typo.caption" Color="Color.Success">✓ Saved</MudText>
    }
</EditForm>

@code {
    private EditContext editContext = null!;
    private System.Threading.Timer? autoSaveTimer;
    private const int AutoSaveDelayMs = 2000;
    private string autoSaveStatus = "";

    protected override void OnInitialized()
    {
        editContext = new EditContext(recipe);
    }

    private void ScheduleAutoSave()
    {
        autoSaveTimer?.Dispose();
        autoSaveTimer = new System.Threading.Timer(async _ =>
        {
            if (editContext.IsModified())
            {
                await InvokeAsync(async () =>
                {
                    autoSaveStatus = "Saving...";
                    StateHasChanged();

                    await DraftService.SaveDraftAsync(recipe);
                    editContext.MarkAsUnmodified();

                    autoSaveStatus = "Saved";
                    StateHasChanged();

                    await Task.Delay(2000); // Show "Saved" for 2 seconds
                    autoSaveStatus = "";
                    StateHasChanged();
                });
            }
        }, null, AutoSaveDelayMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        autoSaveTimer?.Dispose();
    }
}
```

### Blazor-DragDrop with Ingredient Reordering

```razor
@* Source: https://github.com/Postlagerkarte/blazor-dragdrop *@
@using Plk.Blazor.DragDrop

<Dropzone Items="ingredients"
          InstantReplace="true"
          TItem="Ingredient"
          OnItemDrop="OnIngredientReordered">
    <ChildContent>
        @foreach (var ingredient in context)
        {
            <MudPaper Class="pa-2 ma-1 d-flex justify-space-between" Elevation="1">
                <MudText>@FormatIngredient(ingredient)</MudText>
                <MudIconButton Icon="@Icons.Material.Filled.Delete"
                              Size="Size.Small"
                              OnClick="() => DeleteIngredient(ingredient)" />
            </MudPaper>
        }
    </ChildContent>
</Dropzone>

@code {
    private void OnIngredientReordered(Ingredient item)
    {
        // Update order in database
        for (int i = 0; i < ingredients.Count; i++)
        {
            ingredients[i].Order = i;
        }
        editContext.NotifyFieldChanged(editContext.Field(nameof(recipe.Ingredients)));
        ScheduleAutoSave();
    }

    private string FormatIngredient(Ingredient ing)
    {
        var quantity = ing.Quantity.ToMixedNumberString();
        return $"{quantity} {ing.Unit} {ing.Name}";
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| InputFile without streaming | InputFile with OpenReadStream() | .NET 5 (2020) | Must use streaming in Blazor Server to avoid memory issues |
| EditForm with manual validation | EditForm with DataAnnotationsValidator | Blazor 3.0 (2019) | Standard validation pattern, no manual implementation needed |
| JavaScript interop for drag-drop | blazor-dragdrop library | 2021 | Simplifies drag-drop, but mobile still needs polyfill |
| CSS classes for theming | MudBlazor theme system with CSS variables | MudBlazor v6+ (2022) | Centralized theming, easier dark mode |
| MemoryCache for drafts | EF Core with DraftRecipe table | - | Drafts survive server restart, per-user isolation with HouseholdId |
| NavigationManager.LocationChanging | NavigationLock component | .NET 8 (2023) | Declarative navigation blocking, simpler than event handler |
| Manual EditContext dirty tracking | EditContext.IsModified() | .NET 6 (2021) | Built-in method, no custom tracking needed |

**Deprecated/outdated:**
- **blazor-dragdrop fork by nicolassargos**: Original repo is Postlagerkarte/blazor-dragdrop. nicolassargos is a fork with no updates since 2021.
- **MatBlazor**: No longer maintained (last update 2021). Use MudBlazor instead.
- **Storing file upload in string (ReadToEndAsync)**: Never recommended, but older tutorials show this. Always stream to FileStream.
- **HTML `<input type="file">` without InputFile component**: Blazor's InputFile handles SignalR chunking correctly. Raw input doesn't.

## Open Questions

Things that couldn't be fully resolved:

1. **Ingredient Parsing Library**
   - What we know: No mature C# NuGet package found. Python library (strangetom/ingredient-parser) exists and is well-tested. JavaScript library (sharp-recipe-parser) exists on npm.
   - What's unclear: Whether to port Python library, port JavaScript library, or build custom C# parser from scratch.
   - Recommendation: Build custom C# parser using rule-based state machine (not regex). Reference Python library for test cases and edge cases. Use Sprache or Pidgin parser combinator library if complexity grows. Budget 2-3 days for parser development and testing. Mark as technical risk.

2. **Image Thumbnail Strategy**
   - What we know: SixLabors.ImageSharp can generate thumbnails server-side. CSS can crop/resize images client-side. Storing original only is simplest.
   - What's unclear: Whether thumbnail generation is needed for performance (page load time with 50+ recipe cards showing images).
   - Recommendation: Phase 2 implementation: store original only, use CSS `object-fit: cover` for card images. If performance issue arises (slow page load), add thumbnail generation in later phase. ImageSharp thumbnail generation is 1-2 hours work if needed. Defer decision until performance measured.

3. **Mobile Drag-Drop Polyfill Reliability**
   - What we know: blazor-dragdrop requires mobile-drag-drop.js polyfill for mobile support. Polyfill exists and is referenced in docs.
   - What's unclear: Whether polyfill works reliably across iOS Safari, Android Chrome, and various mobile browsers in 2026. Docs don't specify browser versions tested.
   - Recommendation: Test on actual devices (iOS Safari, Android Chrome) during implementation. If polyfill unreliable, add fallback: "Reorder" button opens modal with up/down buttons (no drag-drop). Desktop users get drag-drop, mobile users get button-based reordering.

4. **Undo Toast Duration**
   - What we know: Context specifies "undo toast (few seconds to recover)" but doesn't specify exact duration. Standard toast duration is 3-5 seconds. Undo requires user to notice, read, and click within timeout.
   - What's unclear: Optimal duration for undo action (too short = user can't react, too long = clutters UI).
   - Recommendation: Implement with 5 seconds timeout (industry standard for undo toasts). Make configurable in toast service for easy adjustment based on user testing. If users report missing undo, increase to 7-10 seconds.

5. **Category Icon Storage**
   - What we know: Context specifies categories have "colors AND icons (emoji or SVG)". Unclear whether icons are:
     - Emoji stored as Unicode string (simple, cross-platform, limited selection)
     - SVG stored as text (complex, requires sanitization, unlimited customization)
     - SVG uploaded as files (most complex, highest flexibility)
   - What's unclear: Which icon approach to use.
   - Recommendation: Start with emoji Unicode (simplest). Store as string in Category table (`IconEmoji` column). Users select from predefined list (🥩🥬🧈🥫🧂❄️🍞🥤). If users request custom icons later, add `IconSvgPath` column for SVG file paths. Emoji covers 90% of use cases with zero complexity.

## Sources

### Primary (HIGH confidence)

- Microsoft Learn - ASP.NET Core Blazor file uploads: https://learn.microsoft.com/en-us/aspnet/core/blazor/file-uploads?view=aspnetcore-10.0
- Microsoft Learn - ASP.NET Core Blazor forms validation: https://learn.microsoft.com/en-us/aspnet/core/blazor/forms/validation?view=aspnetcore-10.0
- MudBlazor official documentation: https://mudblazor.com/
- MudBlazor Autocomplete: https://mudblazor.com/components/autocomplete
- Markdig GitHub repository: https://github.com/xoofx/markdig
- blazor-dragdrop GitHub repository: https://github.com/Postlagerkarte/blazor-dragdrop
- Fractions NuGet package: https://www.nuget.org/packages/fractions/

### Secondary (MEDIUM confidence)

- MudBlazor vs Radzen comparison: https://gimburg.online/mudblazor-vs-radzen-choosing-the-right-component-library-for-your-blazor-project/
- Fluent UI vs MudBlazor vs Radzen (Medium article): https://medium.com/net-code-chronicles/fluentui-vs-mudblazor-vs-radzen-ae86beb3e97b
- Blazor Basics: Uploading Files in Blazor Server: https://www.telerik.com/blogs/blazor-basics-uploading-files-blazor-server-web-applications
- Implementing Soft Delete With EF Core: https://www.milanjovanovic.tech/blog/implementing-soft-delete-with-ef-core
- Soft Delete in EF Core (Medium article): https://medium.com/@kittikawin_ball/soft-delete-in-ef-core-patterns-for-safer-data-management-1d61fec4347b
- An Easier Blazor Debounce: https://blog.jeremylikness.com/blog/an-easier-blazor-debounce/
- Blazor Markdown Editor (Syncfusion): https://www.syncfusion.com/blogs/post/blazor-live-preview-markdown-editors-content-using-markdig-library
- SixLabors ImageSharp documentation: https://docs.sixlabors.com/articles/imagesharp/resize.html
- Dark mode with Blazor and Tailwind CSS: https://jonhilton.net/blazor-tailwind-dark-mode/

### Tertiary (LOW confidence - requires validation)

- Recipe ingredient parsers (Python): https://github.com/strangetom/ingredient-parser (well-documented but Python, would need porting)
- Recipe ingredient parser (C#/TypeScript): https://github.com/jlucaspains/sharp-recipe-parser (JavaScript, not C#, misleading name)
- Blazor toast undo pattern: Multiple sources mention toast notifications but specific undo pattern implementation not verified in official docs
- PostgreSQL bytea mapping in EF Core: Inferred from SQL Server varbinary examples, not PostgreSQL-specific documentation

## Metadata

**Confidence breakdown:**
- Standard stack (MudBlazor, Markdig, Fractions): HIGH - Official documentation, established libraries with 10M+ downloads, current versions verified
- Architecture patterns (file upload streaming, soft delete, auto-save): HIGH - Microsoft official docs, established EF Core patterns
- Ingredient parsing: LOW - No C# library found, custom implementation required, complexity uncertain
- Image thumbnails: MEDIUM - ImageSharp verified, but decision on whether to use deferred to implementation
- Mobile drag-drop: MEDIUM - Polyfill documented but cross-browser reliability not verified
- Dark mode implementation: MEDIUM - MudBlazor supports it, but specific color palette choices are Claude's discretion

**Research date:** 2026-01-23
**Valid until:** ~60 days (stable ecosystem, MudBlazor/Markdig/EF Core don't change rapidly)
**Re-research needed if:**
- .NET 9 releases (breaking changes to Blazor)
- MudBlazor v8 releases (API changes)
- Ingredient parsing becomes blocker (investigate commercial parsing APIs or ML-based approaches)
