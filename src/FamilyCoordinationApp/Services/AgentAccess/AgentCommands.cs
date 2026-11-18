using System.Globalization;
using FamilyCoordinationApp.Constants;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>
/// The agent commands (quest 8bfa42cf, design §5) over the app's own services and tenant pipeline: five reads and
/// six bounded writes. No deletes, no bulk edits, no regeneration; the one replacement is a recipe's image, and only
/// when the caller names the version it read. Each write goes through <see cref="AgentCommandRunner"/> (one
/// transaction with its idempotency record; a dry run is rolled back).
/// <para>Images: an agent passes an image URL, never a path. The server fetches it through
/// <see cref="SafeRecipeFetcher"/> (public addresses only, one deadline, a size cap, the bytes' own signature), stores
/// its own copy in the caller household's upload directory through <see cref="IImageService"/> (the UI upload's
/// writer), and the recipe points at that copy, so nothing hot-links. The fetch happens before the write's
/// transaction (a slow site never holds the household's write lock), after a retry has been answered from its record
/// (a retry never fetches again); the file is written inside the write, and deleted if the write does not commit.</para>
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
    IImageService images,
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
            .Select(r => new RecipeSummary(r.RecipeId, r.Name, r.RecipeType, r.Servings, r.PrepTimeMinutes, r.CookTimeMinutes, r.Ingredients.Count,
                !string.IsNullOrWhiteSpace(r.ImagePath)))
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

    public async Task<AgentWriteOutcome<CreatedRecipe>> CreateRecipeAsync(CreateRecipeInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, userId) = Caller;
        var recipe = BuildRecipe(input, householdId, userId);
        var imageUrl = ImageUrl(input.ImageUrl, required: false);

        FetchedImage? image = null;
        if (imageUrl is not null)
        {
            if (await runner.TryReplayAsync<CreatedRecipe>("create_recipe", requestId, input, dryRun, ct) is { } replayed) return replayed;
            image = await FetchImageAsync(imageUrl, ct);
        }

        string? stored = null;
        try
        {
            return await runner.RunAsync("create_recipe", requestId, input, dryRun, async token =>
            {
                if (image is not null && !dryRun)
                {
                    stored = await StoreImageAsync(image, householdId, token);
                    recipe.ImagePath = stored;
                }
                var saved = await recipes.CreateRecipeAsync(recipe, token);
                // Read the committed version back inside the same transaction: the row's xmin is what an edit sends.
                await using var context = await dbFactory.CreateDbContextAsync(token);
                var version = await context.Recipes.Where(r => r.RecipeId == saved.RecipeId).Select(r => r.Version).FirstAsync(token);
                var fetched = image is null ? null : new AgentImage(image.ContentType, image.Bytes.Length);
                return dryRun
                    ? new CreatedRecipe(null, saved.Name, null, saved.Ingredients.Count, null, null, fetched)
                    : new CreatedRecipe(saved.RecipeId, saved.Name, version, saved.Ingredients.Count, "/recipes", stored, fetched);
            }, ct);
        }
        catch
        {
            await DiscardImageAsync(stored, householdId);
            throw;
        }
    }

    /// <summary>
    /// Give an existing recipe a picture from <see cref="SetRecipeImageInput.ImageUrl"/>. Without a version this only
    /// fills an EMPTY image (the add-only rule: a recipe that has one is a conflict and keeps it). With the version
    /// the caller read from get_recipe, it sets or replaces the image, checked against that version. The previous
    /// file is kept, as the app's own editor keeps it.
    /// </summary>
    public async Task<AgentWriteOutcome<SetRecipeImageResult>> SetRecipeImageAsync(SetRecipeImageInput input, string? requestId, bool dryRun, CancellationToken ct = default)
    {
        var (householdId, _) = Caller;
        var imageUrl = ImageUrl(input.ImageUrl, required: true)!;
        if (await runner.TryReplayAsync<SetRecipeImageResult>("set_recipe_image", requestId, input, dryRun, ct) is { } replayed) return replayed;

        // The cheap refusals (no such recipe; it already has an image and no version was given) come before the fetch.
        await CheckImageTargetAsync(input, ct);
        var image = await FetchImageAsync(imageUrl, ct);

        string? stored = null;
        try
        {
            return await runner.RunAsync("set_recipe_image", requestId, input, dryRun, async token =>
            {
                // Read again under the write lock: the recipe may have changed while the image was fetched.
                var (previous, current) = await CheckImageTargetAsync(input, token);
                uint? version = null;
                // A dry run skips the write, so it must check the caller's version itself: the save's version check
                // is the only other one, and a preview must not report success for a write that would be refused.
                if (dryRun && input.Version is { } given) ConcurrencySave.ThrowIfStale<Recipe>(current, given);
                if (!dryRun)
                {
                    stored = await StoreImageAsync(image, householdId, token);
                    // No version: the one just read, so an image added meanwhile by the app is a stale-version conflict.
                    await recipes.SetImagePathAsync(input.RecipeId, stored, input.Version ?? current, token);
                    await using var context = await dbFactory.CreateDbContextAsync(token);
                    version = await context.Recipes.Where(r => r.RecipeId == input.RecipeId).Select(r => r.Version).FirstAsync(token);
                }
                return new SetRecipeImageResult(
                    input.RecipeId,
                    previous is null ? "added" : "replaced",
                    stored,
                    previous,
                    version,
                    new AgentImage(image.ContentType, image.Bytes.Length),
                    "/recipes");
            }, ct);
        }
        catch
        {
            await DiscardImageAsync(stored, householdId);
            throw;
        }
    }

    /// <summary>The recipe's current image and version, or the refusal: not in this household, or (no version given) it already has an image.</summary>
    private async Task<(string? ImagePath, uint Version)> CheckImageTargetAsync(SetRecipeImageInput input, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var current = await context.Recipes.AsNoTracking()
            .Where(r => r.RecipeId == input.RecipeId)
            .Select(r => new { r.ImagePath, r.Version })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Recipe {input.RecipeId} was not found in your household. Use search_recipes to find one.");
        var previous = string.IsNullOrWhiteSpace(current.ImagePath) ? null : current.ImagePath;
        if (previous is not null && input.Version is null)
        {
            throw new ConflictException(
                $"Recipe {input.RecipeId} already has an image, so nothing was changed. To replace it, call set_recipe_image " +
                "again with the recipe's current version from get_recipe.");
        }
        return (previous, current.Version);
    }

    private static string? ImageUrl(string? value, bool required)
    {
        var url = required
            ? Required(value, "imageUrl", FieldLengths.Recipe.SourceUrl)
            : Optional(value, "imageUrl", FieldLengths.Recipe.SourceUrl);
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            throw new DomainValidationException("imageUrl must be an absolute http or https URL of the image itself.");
        }
        return url;
    }

    private async Task<FetchedImage> FetchImageAsync(string url, CancellationToken ct)
    {
        try
        {
            return await fetcher.FetchImageAsync(url, ct);
        }
        catch (RecipeFetchException ex)
        {
            throw new DomainValidationException($"The image could not be used: {ex.Message} Nothing was saved.");
        }
    }

    /// <summary>Write the server's own copy and return its path, which must be an upload of the caller's household.</summary>
    private async Task<string> StoreImageAsync(FetchedImage image, int householdId, CancellationToken ct)
    {
        var path = await images.SaveImageBytesAsync(image.Bytes, householdId, ct);
        if (!ImagePathPolicy.TryNormalize(path, householdId, out var normalized) || normalized != path || !path.StartsWith($"/uploads/{householdId}/", StringComparison.Ordinal))
        {
            await images.DeleteImageAsync(path, householdId, CancellationToken.None);
            throw new InvalidOperationException("The stored image path is not an upload of the caller's household.");
        }
        return path;
    }

    /// <summary>A write that did not commit leaves no file behind. The delete is confined to the household's directory.</summary>
    private Task DiscardImageAsync(string? stored, int householdId) =>
        stored is null ? Task.CompletedTask : images.DeleteImageAsync(stored, householdId, CancellationToken.None);

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

        FetchedPage fetched;
        try
        {
            fetched = await fetcher.FetchPageAsync(url, ct);
        }
        catch (RecipeFetchException ex)
        {
            throw new DomainValidationException($"The page could not be fetched: {ex.Message} No recipe was saved.");
        }

        var schema = await scraper.ExtractJsonLdAsync(fetched.Html, ct);
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
            "Nothing was saved. To keep this recipe, call create_recipe with these fields (imageUrl included, if one was found).",
            AgentContent.UntrustedNote,
            // Relative images resolve against the address the page was finally served from (after redirects).
            PreviewImageUrl(fetched.FinalUrl.AbsoluteUri, recipe.ImagePath));
    }

    /// <summary>
    /// The page's schema.org image as an absolute http(s) URL create_recipe can fetch, or null. A relative value
    /// ("/images/pie.jpg", "pie.jpg", "//cdn.example/pie.jpg") is resolved against the page URL, as a browser would;
    /// any other scheme or an over-long result is left out rather than passed on.
    /// </summary>
    internal static string? PreviewImageUrl(string pageUrl, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        // Absolute first, and only http(s): on Unix "/images/pie.jpg" parses as an absolute file: URI, so a relative
        // reference is parsed explicitly as Relative and combined with the page URL.
        Uri? resolved = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
        {
            resolved = absolute;
        }
        else if (Uri.TryCreate(value, UriKind.Relative, out var relative) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
        {
            resolved = new Uri(page, relative);
        }
        if (resolved is null || (resolved.Scheme != Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttp)) return null;
        var text = resolved.AbsoluteUri;
        return text.Length <= FieldLengths.Recipe.SourceUrl ? text : null;
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
        AgentContent.UntrustedNote,
        string.IsNullOrWhiteSpace(recipe.ImagePath) ? null : recipe.ImagePath);

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
