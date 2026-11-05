using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.AgentAccess;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The agent commands (quest 8bfa42cf, design §7 PR 2) on the real host and Postgres, each run as a resolved caller
/// in its own DI scope, the way an /mcp request runs them: insert-only meal adds, request idempotency (replay, the
/// simultaneous identical key, the changed payload), one transaction per write (rollback, dry runs that write
/// nothing), household isolation, and the week's list built from scaled meals.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AgentCommandsTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    // A Monday well clear of the fixture's seeded data.
    private static readonly DateOnly Week = new(2026, 11, 9);

    private const int A = ChoresWebAppFactory.HouseholdAId;
    private const int UserA = ChoresWebAppFactory.UserAId;
    private const int B = ChoresWebAppFactory.HouseholdBId;
    private const int UserB = ChoresWebAppFactory.UserBId;

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> As<T>(int householdId, int userId, Func<AgentCommands, Task<T>> run)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCaller(householdId, userId);
        return await run(scope.ServiceProvider.GetRequiredService<AgentCommands>());
    }

    private ApplicationDbContext Raw() => new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContext();

    private static CreateRecipeInput Chili(string name = "MCP acceptance chili") => new(
        name, RecipeType.Main, "A test chili.", "Simmer everything.", null, Servings: 4, PrepTimeMinutes: 10, CookTimeMinutes: 60,
        [
            new CreateRecipeIngredient("black beans", 2m, "cup", "Pantry", null, null),
            new CreateRecipeIngredient("onion", 1m, null, null, "diced", null),
        ]);

    private async Task<int> CreateChiliAsync(int householdId, int userId, string requestId)
    {
        var created = await As(householdId, userId, c => c.CreateRecipeAsync(Chili(), requestId, dryRun: false));
        return created.Result.RecipeId!.Value;
    }

    private async Task<(int Recipes, int Entries, int Lists, int Items, int Operations, int Plans)> CountsAsync(int householdId)
    {
        await using var db = Raw();
        return (
            await db.Recipes.CountAsync(r => r.HouseholdId == householdId),
            await db.MealPlanEntries.CountAsync(e => e.HouseholdId == householdId),
            await db.ShoppingLists.CountAsync(l => l.HouseholdId == householdId),
            await db.ShoppingListItems.CountAsync(i => i.HouseholdId == householdId),
            await db.AgentOperations.CountAsync(o => o.HouseholdId == householdId),
            await db.MealPlans.CountAsync(p => p.HouseholdId == householdId));
    }

    // ── The acceptance scenario's arithmetic: 2 cups × 8/4 × 7 dinners = 28 cups ─────────────────

    [Fact]
    public async Task A_week_of_scaled_dinners_builds_one_list_with_28_cups_of_beans()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-0001");
        for (var day = 0; day < 7; day++)
        {
            var added = await As(A, UserA, c => c.AddMealAsync(
                new AddMealInput(Week.AddDays(day), MealType.Dinner, chili, null, null), $"meal-{day:0000}", dryRun: false));
            added.Result.Outcome.Should().Be("added");
            var meal = added.Result.Meal;
            await As(A, UserA, c => c.SetMealServingsAsync(
                new SetMealServingsInput(meal.MealPlanId, meal.EntryId, 8, meal.Version), $"serve-{day:0000}", dryRun: false));
        }
        await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Lunch, null, "Leftovers", null), "custom-0001", dryRun: false));
        // The following Monday is outside the week and must not be counted.
        await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week.AddDays(7), MealType.Dinner, chili, null, null), "next-week-01", dryRun: false));

        var generated = await As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week, null), "list-0001", dryRun: false));

        var result = generated.Result;
        result.ListId.Should().NotBeNull();
        result.StartDate.Should().Be(Week);
        result.EndDate.Should().Be(Week.AddDays(6));
        result.IncludedMeals.Should().HaveCount(7).And.OnlyContain(m => m.ScaleFactor == 2m && m.Servings == 8);
        result.SkippedMeals.Should().ContainSingle(m => m.Name == "Leftovers");
        result.PantryAdjusted.Should().BeFalse();
        result.Items.Should().ContainSingle(i => i.Name.Contains("black beans", StringComparison.OrdinalIgnoreCase))
            .Which.Quantity.Should().Be(28m);
        result.Items.Should().ContainSingle(i => i.Name.Contains("onion", StringComparison.OrdinalIgnoreCase))
            .Which.Quantity.Should().Be(14m);

        var read = await As(A, UserA, c => c.GetShoppingListsAsync(new GetShoppingListsInput(result.ListId, false, null, null)));
        read.Detail!.Items.Should().Contain(i => i.Quantity == 28m);
    }

    [Fact]
    public async Task Generating_needs_a_Monday_and_a_planned_week()
    {
        var notMonday = () => As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week.AddDays(1), null), "list-tue-01", false));
        await notMonday.Should().ThrowAsync<DomainValidationException>().WithMessage("*must be a Monday*");

        var before = await CountsAsync(A);
        var empty = () => As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week.AddDays(21), null), "list-empty-01", false));
        await empty.Should().ThrowAsync<DomainValidationException>().WithMessage("*No meals are planned*");
        (await CountsAsync(A)).Should().Be(before, "a refused generation writes nothing, not even an empty plan");
    }

    // ── Insert-only adds ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_app_fold_overwrites_notes_but_the_agent_add_never_does()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-fold-01");
        var first = await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, "Amy's birthday"), "fold-0001", false));
        var entry = first.Result.Meal;

        // Control (the existing UI path): a fold with other notes overwrites them silently.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCaller(A, UserA);
            await scope.ServiceProvider.GetRequiredService<IMealPlanService>()
                .AddMealAsync(A, Week, MealType.Dinner, chili, null, "overwritten by the fold", UserA);
        }
        await using (var db = Raw())
        {
            (await db.MealPlanEntries.SingleAsync(e => e.HouseholdId == A && e.EntryId == entry.EntryId && e.MealPlanId == entry.MealPlanId))
                .Notes.Should().Be("overwritten by the fold");
            await db.MealPlanEntries.Where(e => e.HouseholdId == A && e.EntryId == entry.EntryId && e.MealPlanId == entry.MealPlanId)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Notes, "Amy's birthday"));
        }

        // The agent add: a duplicate with different notes is a conflict and changes nothing…
        var differing = () => As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, "taco night instead"), "fold-0002", false));
        (await differing.Should().ThrowAsync<ConflictException>()).Which.ClientMessage.Should().Match("*already planned*Nothing was changed*");

        // …and one without notes (or with the same notes) returns the entry unchanged.
        var same = await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "fold-0003", false));
        same.Result.Outcome.Should().Be("alreadyPlanned");
        same.Result.Meal.EntryId.Should().Be(entry.EntryId);
        same.Result.Meal.Notes.Should().Be("Amy's birthday");

        await using var check = Raw();
        (await check.MealPlanEntries.CountAsync(e => e.HouseholdId == A && e.Date == Week && e.MealType == MealType.Dinner)).Should().Be(1);
        (await check.MealPlanEntries.SingleAsync(e => e.HouseholdId == A && e.EntryId == entry.EntryId && e.MealPlanId == entry.MealPlanId))
            .Notes.Should().Be("Amy's birthday");
    }

    [Fact]
    public async Task Two_concurrent_identical_adds_with_different_keys_make_one_entry()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-race-01");
        var adds = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week.AddDays(2), MealType.Dinner, chili, null, null), $"race-key-{i:00}", false))));

        adds.Count(a => a.Result.Outcome == "added").Should().Be(1);
        adds.Select(a => a.Result.Meal.EntryId).Distinct().Should().ContainSingle();
        await using var db = Raw();
        (await db.MealPlanEntries.CountAsync(e => e.HouseholdId == A && e.Date == Week.AddDays(2))).Should().Be(1);
    }

    // ── Idempotency ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lost_response_retried_with_the_same_key_replays_without_writing_again()
    {
        var first = await As(A, UserA, c => c.CreateRecipeAsync(Chili(), "retry-recipe-01", false));
        var after = await CountsAsync(A);

        var retry = await As(A, UserA, c => c.CreateRecipeAsync(Chili(), "retry-recipe-01", false));

        retry.Replayed.Should().BeTrue();
        retry.Result.Should().BeEquivalentTo(first.Result);
        (await CountsAsync(A)).Should().Be(after);
    }

    [Fact]
    public async Task Simultaneous_requests_with_one_key_run_once()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            As(A, UserA, c => c.CreateRecipeAsync(Chili("Same-key chili"), "same-key-0001", false))));

        outcomes.Count(o => !o.Replayed).Should().Be(1);
        outcomes.Select(o => o.Result.RecipeId).Distinct().Should().ContainSingle();
        await using var db = Raw();
        (await db.Recipes.CountAsync(r => r.HouseholdId == A && r.Name == "Same-key chili")).Should().Be(1);
        (await db.AgentOperations.CountAsync(o => o.RequestId == "same-key-0001")).Should().Be(1);
    }

    [Fact]
    public async Task A_reused_key_with_a_different_payload_is_a_conflict_and_changes_nothing()
    {
        await As(A, UserA, c => c.CreateRecipeAsync(Chili("First"), "reused-key-01", false));
        var before = await CountsAsync(A);

        var reuse = () => As(A, UserA, c => c.CreateRecipeAsync(Chili("Second"), "reused-key-01", false));

        await reuse.Should().ThrowAsync<ConflictException>().WithMessage("*already used for a different change*");
        (await CountsAsync(A)).Should().Be(before);
    }

    [Fact]
    public async Task Keys_are_per_member_so_another_member_may_reuse_one()
    {
        await As(A, UserA, c => c.CreateRecipeAsync(Chili("Alice's"), "shared-key-01", false));
        var amy = await As(A, ChoresWebAppFactory.UserA2Id, c => c.CreateRecipeAsync(Chili("Amy's"), "shared-key-01", false));
        amy.Replayed.Should().BeFalse();
    }

    [Fact]
    public async Task A_malformed_request_id_is_refused_before_anything_runs()
    {
        var bad = () => As(A, UserA, c => c.CreateRecipeAsync(Chili(), "short", false));
        await bad.Should().ThrowAsync<DomainValidationException>().WithMessage("*requestId*");
    }

    // ── One transaction: rollback and dry runs ───────────────────────────────────────────────

    [Fact]
    public async Task A_write_that_fails_partway_rolls_back_its_rows_and_its_record()
    {
        var before = await CountsAsync(A);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCaller(A, UserA);
            var runner = scope.ServiceProvider.GetRequiredService<AgentCommandRunner>();
            var recipes = scope.ServiceProvider.GetRequiredService<IRecipeService>();
            var failing = () => runner.RunAsync<int>("create_recipe", "partial-0001", new { n = 1 }, dryRun: false, async ct =>
            {
                await recipes.CreateRecipeAsync(new Recipe { HouseholdId = A, Name = "Half-written", RecipeType = RecipeType.Main }, ct);
                throw new InvalidOperationException("injected fault after the first write");
            });
            await failing.Should().ThrowAsync<InvalidOperationException>();
        }

        (await CountsAsync(A)).Should().Be(before);
        await using var db = Raw();
        (await db.Recipes.AnyAsync(r => r.Name == "Half-written")).Should().BeFalse();
    }

    [Fact]
    public async Task Dry_runs_go_through_the_domain_code_and_write_nothing()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-dry-01");
        await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "dry-setup-01", false));
        var before = await CountsAsync(A);
        var emptyWeek = Week.AddDays(35);

        var recipe = await As(A, UserA, c => c.CreateRecipeAsync(Chili("Dry chili"), "dry-recipe-01", dryRun: true));
        var meal = await As(A, UserA, c => c.AddMealAsync(new AddMealInput(emptyWeek, MealType.Dinner, chili, null, null), "dry-meal-01", dryRun: true));
        var list = await As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week, null), "dry-list-01", dryRun: true));

        recipe.DryRun.Should().BeTrue();
        recipe.Result.RecipeId.Should().BeNull();
        meal.Result.Outcome.Should().Be("added", "the preview runs the real insert-only command");
        list.Result.ListId.Should().BeNull();
        list.Result.Items.Should().Contain(i => i.Quantity == 2m);
        (await CountsAsync(A)).Should().Be(before, "a dry run creates no recipe, no meal plan for the new week, no entry, no list and no record");

        // The same key then commits for real: a dry run never claims it.
        var real = await As(A, UserA, c => c.CreateRecipeAsync(Chili("Dry chili"), "dry-recipe-01", dryRun: false));
        real.Replayed.Should().BeFalse();
        real.Result.RecipeId.Should().NotBeNull();
    }

    // ── Household isolation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_households_ids_are_not_found_and_nothing_crosses()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-iso-01");
        var meal = (await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "iso-meal-01", false))).Result.Meal;
        var list = (await As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week, null), "iso-list-01", false))).Result;
        var beforeA = await CountsAsync(A);
        var beforeB = await CountsAsync(B);

        await FluentActions.Awaiting(() => As(B, UserB, c => c.GetRecipeAsync(chili))).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(B, UserB, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "iso-b-meal1", false)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(B, UserB, c => c.SetMealServingsAsync(new SetMealServingsInput(meal.MealPlanId, meal.EntryId, 6, meal.Version), "iso-b-serve", false)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(B, UserB, c => c.AddShoppingItemAsync(new AddShoppingItemInput(list.ListId!.Value, "milk", 1, null, null), "iso-b-item1", false)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(B, UserB, c => c.GetShoppingListsAsync(new GetShoppingListsInput(list.ListId, false, null, null))))
            .Should().ThrowAsync<NotFoundException>();
        (await As(B, UserB, c => c.SearchRecipesAsync(new SearchRecipesInput("chili", null, null)))).Recipes.Should().BeEmpty();
        (await As(B, UserB, c => c.GetMealPlanAsync(Week))).Meals.Should().BeEmpty();

        (await CountsAsync(A)).Should().Be(beforeA);
        (await CountsAsync(B)).Should().Be(beforeB);
    }

    // ── Reads, lists and items ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_page_and_project_plain_text()
    {
        for (var i = 0; i < 3; i++) await CreateChiliAsync(A, UserA, $"page-recipe-{i}");
        var first = await As(A, UserA, c => c.SearchRecipesAsync(new SearchRecipesInput("chili", 2, null)));
        first.Recipes.Should().HaveCount(2);
        first.NextCursor.Should().Be("2");
        var second = await As(A, UserA, c => c.SearchRecipesAsync(new SearchRecipesInput("chili", 2, first.NextCursor)));
        second.Recipes.Should().HaveCount(1);
        second.NextCursor.Should().BeNull();

        var detail = await As(A, UserA, c => c.GetRecipeAsync(first.Recipes[0].RecipeId));
        detail.Ingredients.Should().Contain(i => i.Name == "onion" && i.Notes == "diced");
        detail.Ingredients.Single(i => i.Name == "onion").Category.Should().NotBeNullOrEmpty("a missing category is inferred");
        detail.ContentNote.Should().Be(AgentContent.UntrustedNote);

        var plan = await As(A, UserA, c => c.GetMealPlanAsync(Week.AddDays(3)));
        plan.WeekStart.Should().Be(Week, "any date reads its Monday–Sunday week");
    }

    [Fact]
    public async Task Items_go_only_onto_active_lists_and_reads_never_fall_back_to_archived()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-items-01");
        await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "items-meal-1", false));
        var listId = (await As(A, UserA, c => c.GenerateShoppingListAsync(new GenerateShoppingListInput(Week, "Week list"), "items-list-1", false))).Result.ListId!.Value;

        var item = await As(A, UserA, c => c.AddShoppingItemAsync(new AddShoppingItemInput(listId, "coffee", 1, "bag", null), "items-add-01", false));
        item.Result.ItemId.Should().NotBeNull();
        item.Result.Category.Should().NotBeNullOrEmpty();

        await using (var db = Raw())
        {
            await db.ShoppingLists.Where(l => l.HouseholdId == A && l.ShoppingListId == listId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.IsArchived, true));
        }

        await FluentActions.Awaiting(() => As(A, UserA, c => c.AddShoppingItemAsync(new AddShoppingItemInput(listId, "tea", null, null, null), "items-add-02", false)))
            .Should().ThrowAsync<ConflictException>().WithMessage("*archived*");
        await FluentActions.Awaiting(() => As(A, UserA, c => c.GetShoppingListsAsync(new GetShoppingListsInput(listId, false, null, null))))
            .Should().ThrowAsync<NotFoundException>().WithMessage("*archived: true*");
        var archived = await As(A, UserA, c => c.GetShoppingListsAsync(new GetShoppingListsInput(listId, true, null, null)));
        archived.Detail!.Items.Should().Contain(i => i.Name == "coffee" && i.IsManuallyAdded);
    }

    [Fact]
    public async Task A_stale_servings_version_is_a_conflict_and_changes_nothing()
    {
        var chili = await CreateChiliAsync(A, UserA, "chili-stale-01");
        var meal = (await As(A, UserA, c => c.AddMealAsync(new AddMealInput(Week, MealType.Dinner, chili, null, null), "stale-meal-1", false))).Result.Meal;
        await As(A, UserA, c => c.SetMealServingsAsync(new SetMealServingsInput(meal.MealPlanId, meal.EntryId, 6, meal.Version), "stale-serve-1", false));

        var stale = () => As(A, UserA, c => c.SetMealServingsAsync(new SetMealServingsInput(meal.MealPlanId, meal.EntryId, 10, meal.Version), "stale-serve-2", false));

        await stale.Should().ThrowAsync<ConcurrencyConflictException>();
        (await As(A, UserA, c => c.GetMealPlanAsync(Week))).Meals.Single(m => m.EntryId == meal.EntryId).Servings.Should().Be(6);
    }

    // ── No model calls on the token path ─────────────────────────────────────────────────────

    [Fact]
    public async Task YouTube_imports_are_refused_without_any_fetch()
    {
        var youtube = () => As(A, UserA, c => c.PreviewRecipeImportAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
        await youtube.Should().ThrowAsync<DomainValidationException>().WithMessage("*YouTube imports are not available to agents*");
    }

    [Fact]
    public void The_agent_commands_cannot_reach_a_model_extractor()
    {
        var dependencies = typeof(AgentCommands).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        dependencies.Should().NotContain([typeof(IGeminiRecipeExtractor), typeof(IYouTubeRecipeExtractor)]);
        // The import service is used only through FromSchema, which validates an already-extracted schema.
        typeof(IRecipeImportService).GetMethod(nameof(IRecipeImportService.FromSchema))!.ReturnType.Should().Be<RecipeImportResult>();
    }
}
