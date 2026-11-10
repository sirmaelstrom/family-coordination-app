using System.Globalization;
using FamilyCoordinationApp.Constants;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>
/// The ten agent commands (quest 8bfa42cf, design §5) over the app's own services and tenant pipeline: five reads and
/// five bounded writes. No deletes, no replacements, no bulk edits, no regeneration. Each write goes through
/// <see cref="AgentCommandRunner"/> (one transaction with its idempotency record; a dry run is rolled back).
/// <para>Every method runs as the request's caller (tenant state Caller): household and user come from there, never
/// from input. Failures are <see cref="DomainException"/>s whose messages say what failed and that nothing changed;
/// the MCP adapter (step 3) turns them into tool errors.</para>
/// </summary>
public sealed class AgentCommands(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ITenantContext tenant,
    IRecipeService recipes,
    IMealPlanService mealPlans,
    IMealPlanBoardService boards,
    IShoppingListService shoppingLists,
    IShoppingListGenerator generator,
    ICategoryInferenceService categories,
    IRecipeScraperService scraper,
    IRecipeImportService importer,
    SafeRecipeFetcher fetcher,
    AgentCommandRunner runner)
{
    public const int MaxRecipeResults = 50;
    public const int DefaultRecipeResults = 20;
    public const int MaxIngredients = 100;
    public const int MaxListResults = 50;
    public const int MaxListItems = 200;

    // Ingredient notes and group names are unbounded text columns; agents get a bound anyway.
    public const int MaxIngredientNotes = 500;
    public const int MaxIngredientGroupName = 100;

    private (int HouseholdId, int UserId) Caller =>
        tenant is { State: TenantState.Caller, HouseholdId: { } householdId, UserId: { } userId }
            ? (householdId, userId)
            : throw new InvalidOperationException("Agent commands run only as a resolved caller.");

    // ── Recipes ─────────────────────────────────────────────────────────────────────────────

    public async Task<SearchRecipesResult> SearchRecipesAsync(SearchRecipesInput input, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        var limit = Math.Clamp(input.Limit ?? DefaultRecipeResults, 1, MaxRecipeResults);
        var offset = ParseCursor(input.Cursor);
        var query = string.IsNullOrWhiteSpace(input.Query) ? null : input.Query.Trim();

        var all = await recipes.GetRecipesAsync(householdId, query, ct);
        var ordered = all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.RecipeId).ToList();
        var page = ordered.Skip(offset).Take(limit)
            .Select(r => new RecipeSummary(r.RecipeId, r.Name, r.RecipeType, r.Servings, r.PrepTimeMinutes, r.CookTimeMinutes, r.Ingredients.Count))
            .ToList();
        var next = offset + page.Count < ordered.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new SearchRecipesResult(page, ordered.Count, next, AgentContent.UntrustedNote);
    }

    public async Task<RecipeDetail> GetRecipeAsync(int recipeId, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        var recipe = await recipes.GetRecipeAsync(householdId, recipeId, ct)
            ?? throw new NotFoundException($"Recipe {recipeId} was not found in your household. Use search_recipes to find one.");
        return ToDetail(recipe);
    }

    public Task<AgentWriteOutcome<CreatedRecipe>> CreateRecipeAsync(CreateRecipeInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, userId) = Caller;
        var recipe = BuildRecipe(input, householdId, userId);
        return runner.RunAsync("create_recipe", requestId, input, dryRun, async token =>
        {
            var saved = await recipes.CreateRecipeAsync(recipe, token);
            // Read the committed version back inside the same transaction: the row's xmin is what an edit sends.
            await using var context = await dbFactory.CreateDbContextAsync(token);
            var version = await context.Recipes.Where(r => r.RecipeId == saved.RecipeId).Select(r => r.Version).FirstAsync(token);
            return dryRun
                ? new CreatedRecipe(null, saved.Name, null, saved.Ingredients.Count, null)
                : new CreatedRecipe(saved.RecipeId, saved.Name, version, saved.Ingredients.Count, "/recipes");
        }, ct);
    }

    private Recipe BuildRecipe(CreateRecipeInput input, int householdId, int userId)
    {
        var name = Required(input.Name, "name", FieldLengths.Recipe.Name);
        if (!Enum.IsDefined(input.RecipeType))
        {
            throw new DomainValidationException("recipeType is not one of the app's recipe types.");
        }
        var ingredients = input.Ingredients ?? [];
        if (ingredients.Count > MaxIngredients)
        {
            throw new DomainValidationException($"A recipe can have at most {MaxIngredients} ingredients; this one has {ingredients.Count}.");
        }
        Positive(input.Servings, "servings");
        NonNegative(input.PrepTimeMinutes, "prepTimeMinutes");
        NonNegative(input.CookTimeMinutes, "cookTimeMinutes");
        var sourceUrl = Optional(input.SourceUrl, "sourceUrl", FieldLengths.Recipe.SourceUrl);
        if (sourceUrl is not null && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp)))
        {
            throw new DomainValidationException("sourceUrl must be an absolute http or https URL.");
        }

        return new Recipe
        {
            HouseholdId = householdId,
            Name = name,
            Description = Optional(input.Description, "description", FieldLengths.Recipe.Description),
            Instructions = Optional(input.Instructions, "instructions", FieldLengths.Recipe.Instructions),
            SourceUrl = sourceUrl,
            Servings = input.Servings,
            PrepTimeMinutes = input.PrepTimeMinutes,
            CookTimeMinutes = input.CookTimeMinutes,
            RecipeType = input.RecipeType,
            ImagePath = null, // no implicit image fetch for agents
            CreatedByUserId = userId,
            Ingredients = ingredients.Select((ingredient, index) =>
            {
                var path = $"ingredients[{index}]";
                if (ingredient is null) throw new DomainValidationException($"{path} is empty.");
                var ingredientName = Required(ingredient.Name, $"{path}.name", FieldLengths.RecipeIngredient.Name);
                if (ingredient.Quantity is < 0) throw new DomainValidationException($"{path}.quantity cannot be negative.");
                return new RecipeIngredient
                {
                    Name = ingredientName,
                    Quantity = ingredient.Quantity,
                    Unit = Optional(ingredient.Unit, $"{path}.unit", FieldLengths.RecipeIngredient.Unit),
                    Category = Optional(ingredient.Category, $"{path}.category", FieldLengths.RecipeIngredient.Category)
                               ?? categories.InferCategory(ingredientName),
                    Notes = Optional(ingredient.Notes, $"{path}.notes", MaxIngredientNotes),
                    GroupName = Optional(ingredient.GroupName, $"{path}.groupName", MaxIngredientGroupName),
                    SortOrder = index,
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// Fetch a public recipe page and extract its schema.org data, without saving. Website JSON-LD only: YouTube (and
    /// its Gemini fallback) is refused, so no model is ever called. Saving is a separate <c>create_recipe</c>.
    /// </summary>
    public async Task<RecipeImportPreview> PreviewRecipeImportAsync(string url, CancellationToken ct = default)
    {
        var (householdId, userId) = Caller;
        if (string.IsNullOrWhiteSpace(url)) throw new DomainValidationException("url is required.");
        url = url.Trim();
        if (url.Length > FieldLengths.Recipe.SourceUrl) throw new DomainValidationException($"url can be at most {FieldLengths.Recipe.SourceUrl} characters.");
        if (YouTubeUrlHelper.IsYouTubeUrl(url))
        {
            throw new DomainValidationException(
                "YouTube imports are not available to agents. Read the video's recipe yourself and save it with create_recipe.");
        }

        string html;
        try
        {
            html = await fetcher.FetchHtmlAsync(url, ct);
        }
        catch (RecipeFetchException ex)
        {
            throw new DomainValidationException($"The page could not be fetched: {ex.Message} No recipe was saved.");
        }

        var schema = await scraper.ExtractJsonLdAsync(html, ct);
        var result = importer.FromSchema(schema, url, householdId, userId);
        if (!result.Success || result.Recipe is null)
        {
            throw new DomainValidationException(
                $"No usable recipe was found on that page: {result.ErrorMessage} No recipe was saved. " +
                "You can read the page yourself and save the recipe with create_recipe.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var existing = await context.Recipes
            .Where(r => r.SourceUrl == url)
            .Select(r => new { r.RecipeId, r.Name })
            .FirstOrDefaultAsync(ct);

        var recipe = result.Recipe;
        return new RecipeImportPreview(
            url,
            recipe.Name,
            recipe.RecipeType,
            recipe.Description,
            recipe.Instructions,
            recipe.Servings,
            recipe.PrepTimeMinutes,
            recipe.CookTimeMinutes,
            recipe.Ingredients.OrderBy(i => i.SortOrder).Select(ToAgentIngredient).ToList(),
            existing?.RecipeId,
            existing?.Name,
            "Nothing was saved. To keep this recipe, call create_recipe with these fields.",
            AgentContent.UntrustedNote);
    }

    // ── Meal plan ───────────────────────────────────────────────────────────────────────────

    public async Task<WeekPlan> GetMealPlanAsync(DateOnly weekStart, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        var monday = mealPlans.GetWeekStartDate(weekStart);
        var board = await boards.GetBoardAsync(householdId, monday, ct);
        return new WeekPlan(
            monday,
            monday.AddDays(6),
            board.MealPlanId,
            board.Entries
                .OrderBy(e => e.Date).ThenBy(e => e.MealType).ThenBy(e => e.EntryId)
                .Select(e => new PlannedMeal(e.MealPlanId, e.EntryId, e.Date, e.MealType, e.Recipe?.RecipeId, e.Recipe?.Name,
                    e.Recipe?.Servings, e.CustomMealName, e.Notes, e.Servings, e.Version))
                .ToList(),
            "/meal-plan",
            AgentContent.UntrustedNote);
    }

    public Task<AgentWriteOutcome<AddMealResult>> AddMealAsync(AddMealInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, userId) = Caller;
        if (!Enum.IsDefined(input.MealType)) throw new DomainValidationException("mealType must be breakfast, lunch, dinner or snack.");
        Optional(input.CustomMealName, "customMealName", FieldLengths.MealPlanEntry.CustomMealName);
        Optional(input.Notes, "notes", FieldLengths.MealPlanEntry.Notes);

        return runner.RunAsync("add_meal", requestId, input, dryRun, async token =>
        {
            var (entry, created) = await mealPlans.AddMealIfAbsentAsync(
                householdId, input.Date, input.MealType, input.RecipeId, input.CustomMealName, input.Notes, userId, input.Servings, token);
            var meal = await ReadMealAsync(entry.MealPlanId, entry.EntryId, token);
            return new AddMealResult(created ? "added" : "alreadyPlanned", meal);
        }, ct);
    }

    public Task<AgentWriteOutcome<SetMealServingsResult>> SetMealServingsAsync(SetMealServingsInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        Positive(input.Servings, "servings");
        return runner.RunAsync("set_meal_servings", requestId, input, dryRun, async token =>
        {
            var before = await ReadMealAsync(input.MealPlanId, input.EntryId, token);
            await mealPlans.SetMealServingsAsync(householdId, input.MealPlanId, input.EntryId, input.Servings, input.Version, token);
            var after = await ReadMealAsync(input.MealPlanId, input.EntryId, token);
            return new SetMealServingsResult(after, before.Servings);
        }, ct);
    }

    private async Task<PlannedMeal> ReadMealAsync(int mealPlanId, int entryId, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var meal = await context.MealPlanEntries
            .AsNoTracking()
            .Where(e => e.MealPlanId == mealPlanId && e.EntryId == entryId)
            .Select(e => new PlannedMeal(e.MealPlanId, e.EntryId, e.Date, e.MealType, e.RecipeId,
                e.Recipe == null ? null : e.Recipe.Name, e.Recipe == null ? null : e.Recipe.Servings,
                e.CustomMealName, e.Notes, e.Servings, e.Version))
            .FirstOrDefaultAsync(ct);
        return meal ?? throw new NotFoundException(
            $"Meal entry {entryId} in plan {mealPlanId} was not found in your household. Read the week with get_meal_plan.");
    }

    // ── Shopping lists ──────────────────────────────────────────────────────────────────────

    public async Task<GetShoppingListsResult> GetShoppingListsAsync(GetShoppingListsInput input, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        if (input.ListId is { } listId)
        {
            var list = await context.ShoppingLists.AsNoTracking()
                .Include(l => l.Items)
                .FirstOrDefaultAsync(l => l.ShoppingListId == listId, ct)
                ?? throw new NotFoundException($"Shopping list {listId} was not found in your household.");
            if (list.IsArchived != input.Archived)
            {
                throw new NotFoundException(list.IsArchived
                    ? $"Shopping list {listId} is archived. Pass archived: true to read it."
                    : $"Shopping list {listId} is active, not archived. Pass archived: false to read it.");
            }
            var items = list.Items
                .OrderBy(i => i.IsChecked).ThenBy(i => i.Category).ThenBy(i => i.SortOrder).ThenBy(i => i.ItemId)
                .ToList();
            var detail = new ShoppingListDetail(
                ToSummary(list),
                items.Take(MaxListItems).Select(i => new AgentListItem(
                    i.ItemId, i.Name, i.Quantity, i.Unit, i.Category, i.IsChecked, i.IsManuallyAdded, i.SourceRecipes, i.Version)).ToList(),
                items.Count,
                items.Count > MaxListItems,
                $"/shopping-list/{list.ShoppingListId}");
            return new GetShoppingListsResult(null, null, null, detail, AgentContent.UntrustedNote);
        }

        var limit = Math.Clamp(input.Limit ?? MaxListResults, 1, MaxListResults);
        var offset = ParseCursor(input.Cursor);
        var lists = await context.ShoppingLists.AsNoTracking()
            .Where(l => l.IsArchived == input.Archived)
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.ShoppingListId)
            .Select(l => new ShoppingListSummary(l.ShoppingListId, l.Name, l.IsArchived, l.IsFavorite, l.MealPlanId,
                l.Items.Count, l.Items.Count(i => !i.IsChecked), l.CreatedAt))
            .ToListAsync(ct);
        var page = lists.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count < lists.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        _ = householdId;
        return new GetShoppingListsResult(page, lists.Count, next, null, AgentContent.UntrustedNote);
    }

    /// <summary>
    /// Build a NEW list from one Monday–Sunday week of the meal plan. The week must be named by its Monday (the app's
    /// generator reads only the plan containing its start date, so a range across weeks would silently drop meals).
    /// Custom meals carry no ingredients and are reported as skipped. Nothing is subtracted for food on hand.
    /// </summary>
    public Task<AgentWriteOutcome<GenerateShoppingListResult>> GenerateShoppingListAsync(GenerateShoppingListInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        if (input.WeekStart.DayOfWeek != DayOfWeek.Monday)
        {
            throw new DomainValidationException(
                $"weekStart must be a Monday; {input.WeekStart:yyyy-MM-dd} is a {input.WeekStart.DayOfWeek}. Lists cover one Monday–Sunday week.");
        }
        var start = input.WeekStart;
        var end = start.AddDays(6);
        var name = Optional(input.Name, "name", FieldLengths.ShoppingList.Name) ?? $"Groceries for the week of {start:MMM d}";

        return runner.RunAsync("generate_shopping_list", requestId, input, dryRun, async token =>
        {
            var board = await boards.GetBoardAsync(householdId, start, token);
            if (board.MealPlanId is not { } mealPlanId || board.Entries.Count == 0)
            {
                throw new DomainValidationException(
                    $"No meals are planned for the week of {start:yyyy-MM-dd}. Nothing was created. Add meals with add_meal first.");
            }

            var meals = board.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType).ToList();
            var included = meals.Where(e => e.Recipe is not null)
                .Select(e => new GeneratedListMeal(e.Date, e.MealType, e.Recipe!.Name, e.Servings, e.Recipe.Servings,
                    ShoppingListGenerator.ScaleFactorFor(e.Servings, e.Recipe.Servings)))
                .ToList();
            var skipped = meals.Where(e => e.Recipe is null)
                .Select(e => new GeneratedListMeal(e.Date, e.MealType, e.CustomMealName ?? "(custom meal)", e.Servings, null, 0m))
                .ToList();

            var list = await generator.GenerateFromMealPlanAsync(householdId, mealPlanId, name, start, end, token);
            var rows = list.Items.OrderBy(i => i.Category).ThenBy(i => i.Name)
                .Select(i => new GeneratedListRow(i.Name, i.Quantity ?? 0m, i.Unit ?? string.Empty, i.Category, i.SourceRecipes))
                .ToList();
            return new GenerateShoppingListResult(
                dryRun ? null : list.ShoppingListId,
                list.Name,
                start,
                end,
                included,
                skipped,
                rows,
                PantryAdjusted: false,
                dryRun ? null : $"/shopping-list/{list.ShoppingListId}",
                "Quantities are scaled to each meal's servings and combined. Nothing was subtracted for food already on hand; " +
                "custom meals add no ingredients.");
        }, ct);
    }

    public Task<AgentWriteOutcome<AddedShoppingItem>> AddShoppingItemAsync(AddShoppingItemInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, userId) = Caller;
        var name = Required(input.Name, "name", FieldLengths.ShoppingListItem.Name);
        var unit = Optional(input.Unit, "unit", FieldLengths.ShoppingListItem.Unit);
        var category = Optional(input.Category, "category", FieldLengths.ShoppingListItem.Category) ?? categories.InferCategory(name);
        if (input.Quantity is <= 0) throw new DomainValidationException("quantity must be positive when given.");

        return runner.RunAsync("add_shopping_item", requestId, input, dryRun, async token =>
        {
            var list = await shoppingLists.GetShoppingListAsync(householdId, input.ListId, token)
                ?? throw new NotFoundException($"Shopping list {input.ListId} was not found in your household. Nothing was added.");
            if (list.IsArchived)
            {
                throw new ConflictException($"Shopping list {input.ListId} is archived, so nothing was added. Add to an active list.");
            }

            var saved = await shoppingLists.AddManualItemAsync(new ShoppingListItem
            {
                HouseholdId = householdId,
                ShoppingListId = input.ListId,
                Name = name,
                Quantity = input.Quantity,
                Unit = unit,
                Category = string.IsNullOrWhiteSpace(category) ? CategoryDefaults.DefaultCategory : category,
                IsManuallyAdded = true,
                AddedByUserId = userId,
            }, token);

            await using var context = await dbFactory.CreateDbContextAsync(token);
            var version = await context.ShoppingListItems
                .Where(i => i.ShoppingListId == input.ListId && i.ItemId == saved.ItemId)
                .Select(i => i.Version).FirstAsync(token);
            return new AddedShoppingItem(input.ListId, dryRun ? null : saved.ItemId, saved.Name, saved.Quantity, saved.Unit, saved.Category,
                dryRun ? null : version);
        }, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static RecipeDetail ToDetail(Recipe recipe) => new(
        recipe.RecipeId,
        recipe.Name,
        recipe.RecipeType,
        recipe.Description,
        recipe.Instructions,
        recipe.Servings,
        recipe.PrepTimeMinutes,
        recipe.CookTimeMinutes,
        recipe.SourceUrl,
        recipe.Version,
        recipe.Ingredients.OrderBy(i => i.SortOrder).Select(ToAgentIngredient).ToList(),
        "/recipes",
        AgentContent.UntrustedNote);

    private static AgentIngredient ToAgentIngredient(RecipeIngredient i) =>
        new(i.Name, i.Quantity, i.Unit, i.Category, i.Notes, i.GroupName);

    private static ShoppingListSummary ToSummary(ShoppingList list) => new(
        list.ShoppingListId, list.Name, list.IsArchived, list.IsFavorite, list.MealPlanId,
        list.Items.Count, list.Items.Count(i => !i.IsChecked), list.CreatedAt);

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : throw new DomainValidationException("cursor is not one this tool returned. Omit it to start from the beginning.");
    }

    private static string Required(string? value, string field, int max) =>
        Optional(value, field, max) ?? throw new DomainValidationException($"{field} is required.");

    private static string? Optional(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max
            ? trimmed
            : throw new DomainValidationException($"{field} can be at most {max} characters; it has {trimmed.Length}.");
    }

    private static void Positive(int? value, string field)
    {
        if (value is <= 0) throw new DomainValidationException($"{field} must be a positive number when given.");
    }

    private static void NonNegative(int? value, string field)
    {
        if (value is < 0) throw new DomainValidationException($"{field} cannot be negative.");
    }
}
