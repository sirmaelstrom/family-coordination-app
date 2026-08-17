using System.Security.Claims;
using FamilyCoordinationApp.Constants;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

public static class ShoppingListEndpoints
{
    public static IEndpointRouteBuilder MapShoppingListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shopping-lists")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapGet("/", GetActiveLists);
        group.MapPost("/", CreateList);
        group.MapPost("/actions/generate-from-meal-plan", GenerateFromMealPlan);
        // Literal segments win over the {listId:int} template, so /archived never collides with it.
        group.MapGet("/archived", GetArchivedLists);
        group.MapGet("/archived/{listId:int}", GetArchivedList);
        group.MapGet("/{listId:int}", GetList);
        group.MapDelete("/{listId:int}", DeleteList);

        group.MapPatch("/{listId:int}/items/{itemId:int}", PatchItem);
        group.MapPost("/{listId:int}/items", AddItem);
        group.MapDelete("/{listId:int}/items/{itemId:int}", DeleteItem);
        group.MapPost("/{listId:int}/items/sort-orders", UpdateSortOrders);

        group.MapPost("/{listId:int}/actions/toggle-favorite", ToggleFavorite);
        group.MapPost("/{listId:int}/actions/archive", ArchiveList);
        group.MapPost("/{listId:int}/actions/restore", RestoreList);
        group.MapPost("/{listId:int}/actions/regenerate", RegenerateList);
        group.MapPost("/{listId:int}/actions/rename", RenameList);
        group.MapPost("/{listId:int}/actions/clear-checked", ClearChecked);

        return app;
    }

    // ─── Lists ────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetActiveLists(
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var lists = await svc.GetActiveShoppingListsAsync(ctx.HouseholdId, ct);
        var summaries = lists
            .OrderByDescending(l => l.IsFavorite)
            .ThenByDescending(l => l.CreatedAt)
            .Select(l => new ShoppingListSummaryDto(
                l.ShoppingListId,
                l.Name,
                l.IsFavorite,
                l.Items.Count,
                l.Items.Count(i => !i.IsChecked)))
            .ToList();

        return Results.Ok(summaries);
    }

    private static async Task<IResult> CreateList(
        CreateListRequest req,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Name is required" });
        }

        var list = await svc.CreateShoppingListAsync(ctx.HouseholdId, req.Name.Trim(), null, ct);
        return Results.Created(
            $"/api/shopping-lists/{list.ShoppingListId}",
            new ShoppingListSummaryDto(list.ShoppingListId, list.Name, list.IsFavorite, 0, 0));
    }

    private static async Task<IResult> GenerateFromMealPlan(
        GenerateRequest req,
        ClaimsPrincipal principal,
        IShoppingListGenerator generator,
        IMealPlanService mealPlanService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        if (req.StartDate == default || req.EndDate == default || req.EndDate < req.StartDate)
        {
            return Results.BadRequest(new { message = "Valid start and end dates required" });
        }

        // Use the meal plan containing the start date (matches Blazor page behavior).
        var weekStart = mealPlanService.GetWeekStartDate(req.StartDate);
        var mealPlan = await mealPlanService.GetOrCreateMealPlanAsync(ctx.HouseholdId, weekStart, ct);

        var listName = string.IsNullOrWhiteSpace(req.Name)
            ? $"Shopping List {req.StartDate:MMM d}"
            : req.Name.Trim();

        var created = await generator.GenerateFromMealPlanAsync(
            ctx.HouseholdId, mealPlan.MealPlanId, listName, req.StartDate, req.EndDate, ct);

        return Results.Created(
            $"/api/shopping-lists/{created.ShoppingListId}",
            new ShoppingListSummaryDto(
                created.ShoppingListId,
                created.Name,
                created.IsFavorite,
                created.Items.Count,
                created.Items.Count(i => !i.IsChecked)));
    }

    private static async Task<IResult> GetList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null || list.IsArchived) return Results.NotFound();

        return Results.Ok(ToListDto(list));
    }

    /// <summary>The past-lists browse read. Favorites-first then CreatedAt desc (service-side sort).</summary>
    private static async Task<IResult> GetArchivedLists(
        bool? favoritesOnly,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var lists = await svc.GetArchivedShoppingListsAsync(ctx.HouseholdId, favoritesOnly, ct);
        var summaries = lists
            .Select(l => new ArchivedListSummaryDto(
                l.ShoppingListId,
                l.Name,
                l.IsFavorite,
                l.Items.Count,
                l.Items.Count(i => !i.IsChecked),
                l.CreatedAt,
                l.MealPlanId != null))
            .ToList();

        return Results.Ok(summaries);
    }

    /// <summary>
    /// Read-only detail for an ARCHIVED list (the pick-items-off surface). <see cref="GetList"/> stays
    /// active-only — its archived→404 is an invariant of the active surface, not an accident.
    /// </summary>
    private static async Task<IResult> GetArchivedList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null || !list.IsArchived)
        {
            return Results.NotFound(new { message = "No archived list with that id." });
        }

        return Results.Ok(ToListDto(list));
    }

    /// <summary>Reopen. Restore only flips IsArchived — deliberately no auto-regenerate; the link is kept.</summary>
    private static async Task<IResult> RestoreList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null || !list.IsArchived)
        {
            return Results.NotFound(new { message = "No archived list with that id." });
        }

        await svc.RestoreShoppingListAsync(ctx.HouseholdId, listId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Permanent delete, server-enforced past-only: deleting is a two-step act (archive first), so a
    /// mis-tapped delete on the active surface cannot destroy a live list.
    /// </summary>
    private static async Task<IResult> DeleteList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null) return Results.NotFound(new { message = "No list with that id." });
        if (!list.IsArchived)
        {
            return Results.Conflict(new { message = "Archive a list before deleting it." });
        }

        await svc.DeleteShoppingListAsync(ctx.HouseholdId, listId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Rebuild the generated rows from the linked meal plan (atomic; checked-state, sort position and
    /// quantity edits carry by normalized name — see ShoppingListGenerator). Does NOT change IsArchived:
    /// regenerate and reopen are orthogonal verbs, mirroring restore's no-auto-regenerate ruling.
    /// </summary>
    private static async Task<IResult> RegenerateList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IShoppingListGenerator generator,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null) return Results.NotFound(new { message = "No list with that id." });
        if (list.MealPlanId is null)
        {
            return Results.Conflict(new { message = "This list is not linked to a meal plan." });
        }

        try
        {
            var updated = await generator.RegenerateShoppingListAsync(ctx.HouseholdId, listId, ct);
            return Results.Ok(ToListDto(updated));
        }
        catch (InvalidOperationException)
        {
            // The MealPlanId pre-check passed but the plan row itself is gone (deleted since) —
            // a client-resolvable state, not a server fault.
            return Results.Conflict(new { message = "The linked meal plan no longer exists." });
        }
    }

    private static async Task<IResult> ToggleFavorite(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        try
        {
            var updated = await svc.ToggleFavoriteAsync(ctx.HouseholdId, listId, ct);
            return Results.Ok(new { id = updated.ShoppingListId, isFavorite = updated.IsFavorite });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ArchiveList(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        try
        {
            await svc.ArchiveShoppingListAsync(ctx.HouseholdId, listId, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> RenameList(
        int listId,
        RenameListRequest req,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Name is required" });
        }

        var archived = await IsListArchivedAsync(dbFactory, ctx.HouseholdId, listId, ct);
        if (archived is null) return Results.NotFound();
        if (archived == true) return ArchivedListConflict;

        try
        {
            var updated = await svc.RenameShoppingListAsync(ctx.HouseholdId, listId, req.Name.Trim(), ct);
            return Results.Ok(new { id = updated.ShoppingListId, name = updated.Name });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ClearChecked(
        int listId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var archived = await IsListArchivedAsync(dbFactory, ctx.HouseholdId, listId, ct);
        if (archived is null) return Results.NotFound();
        if (archived == true) return ArchivedListConflict;

        var removed = await svc.ClearCheckedItemsAsync(ctx.HouseholdId, listId, ct);
        return Results.Ok(new { removed });
    }

    // ─── Items ────────────────────────────────────────────────────────────────

    /// <summary>
    /// null = no such list; true = archived. Archived lists are READ-ONLY for ordinary mutations
    /// (council round 1 on PR #101): the past surface renders them with pick checkboxes only, and the
    /// server enforces what the UI implies. Reopen/delete/regenerate/toggle-favorite are the deliberate
    /// exceptions — they are the past surface's own verbs.
    /// </summary>
    private static async Task<bool?> IsListArchivedAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int householdId, int listId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ShoppingLists
            .Where(l => l.HouseholdId == householdId && l.ShoppingListId == listId)
            .Select(l => (bool?)l.IsArchived)
            .FirstOrDefaultAsync(ct);
    }

    private static readonly IResult ArchivedListConflict =
        Results.Conflict(new { message = "This list is archived — reopen it first." });

    private static async Task<IResult> PatchItem(
        int listId,
        int itemId,
        PatchItemRequest req,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var archived = await IsListArchivedAsync(dbFactory, ctx.HouseholdId, listId, ct);
        if (archived is null) return Results.NotFound();
        if (archived == true) return ArchivedListConflict;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ShoppingListItems
            .Include(i => i.AddedBy)
            .FirstOrDefaultAsync(
                i => i.HouseholdId == ctx.HouseholdId
                    && i.ShoppingListId == listId
                    && i.ItemId == itemId,
                ct);

        if (item is null) return Results.NotFound();

        if (req.IsChecked is not null && req.IsChecked.Value != item.IsChecked)
        {
            item.IsChecked = req.IsChecked.Value;
            item.CheckedAt = req.IsChecked.Value ? DateTime.UtcNow : null;
        }
        if (req.Quantity is not null)
        {
            // Generated rows record the user's edit as a delta over the generator's own number, so a
            // regenerate can re-apply it to the fresh consolidated quantity. Manual rows have no
            // generator baseline — their quantity IS the truth, no delta.
            if (!item.IsManuallyAdded)
            {
                item.QuantityDelta = ComputeQuantityDelta(req.Quantity.Value, item.Quantity, item.QuantityDelta);
            }
            item.Quantity = req.Quantity;
        }
        // A unit or category change on a generated row invalidates the delta — the unit changes what
        // the number MEANS, and category is half the consolidator's carry identity, so the edited row
        // no longer corresponds to the baseline the delta was measured against.
        if (!item.IsManuallyAdded
            && ((req.Unit is not null && req.Unit != item.Unit)
                || (req.Category is not null && req.Category != item.Category)))
        {
            item.QuantityDelta = null;
        }
        if (req.Unit is not null) item.Unit = req.Unit;
        if (req.Name is not null) item.Name = req.Name;
        if (req.Category is not null) item.Category = req.Category;
        item.UpdatedByUserId = ctx.UserId;

        var (success, wasConflict, conflictMessage) =
            await svc.UpdateItemWithConcurrencyAsync(item, ct);

        if (!success)
        {
            return wasConflict
                ? Results.Conflict(new { message = conflictMessage ?? "Concurrency conflict" })
                : Results.NotFound();
        }

        return Results.Ok(ToItemDto(item));
    }

    private static async Task<IResult> AddItem(
        int listId,
        AddItemRequest req,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Name is required" });
        }

        var list = await svc.GetShoppingListAsync(ctx.HouseholdId, listId, ct);
        if (list is null) return Results.NotFound();
        if (list.IsArchived) return ArchivedListConflict;

        var item = new ShoppingListItem
        {
            HouseholdId = ctx.HouseholdId,
            ShoppingListId = listId,
            Name = req.Name.Trim(),
            Quantity = req.Quantity,
            Unit = string.IsNullOrWhiteSpace(req.Unit) ? null : req.Unit.Trim(),
            Category = string.IsNullOrWhiteSpace(req.Category)
                ? CategoryDefaults.DefaultCategory
                : req.Category.Trim(),
            IsManuallyAdded = true,
            AddedByUserId = ctx.UserId,
        };

        var saved = await svc.AddManualItemAsync(item, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var withAuthor = await db.ShoppingListItems
            .Include(i => i.AddedBy)
            .FirstAsync(
                i => i.HouseholdId == ctx.HouseholdId
                    && i.ShoppingListId == listId
                    && i.ItemId == saved.ItemId,
                ct);

        return Results.Created(
            $"/api/shopping-lists/{listId}/items/{withAuthor.ItemId}",
            ToItemDto(withAuthor));
    }

    private static async Task<IResult> DeleteItem(
        int listId,
        int itemId,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        var archived = await IsListArchivedAsync(dbFactory, ctx.HouseholdId, listId, ct);
        if (archived is null) return Results.NotFound();
        if (archived == true) return ArchivedListConflict;

        try
        {
            await svc.DeleteItemAsync(ctx.HouseholdId, listId, itemId, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdateSortOrders(
        int listId,
        UpdateSortOrdersRequest req,
        ClaimsPrincipal principal,
        IShoppingListService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var ctx = await ResolveUserAsync(principal, dbFactory, ct);
        if (ctx is null) return Results.Unauthorized();

        if (req.Updates is null || req.Updates.Count == 0)
        {
            return Results.NoContent();
        }

        var archived = await IsListArchivedAsync(dbFactory, ctx.HouseholdId, listId, ct);
        if (archived is null) return Results.NotFound();
        if (archived == true) return ArchivedListConflict;

        var updates = req.Updates
            .Select(u => (u.ItemId, u.SortOrder, (string?)u.Category))
            .ToList();

        await svc.UpdateItemSortOrdersAsync(ctx.HouseholdId, listId, updates, ct);
        return Results.NoContent();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<UserContext?> ResolveUserAsync(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(email)) return null;

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var user = await context.Users
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.HouseholdId })
            .FirstOrDefaultAsync(ct);

        return user is null ? null : new UserContext(user.HouseholdId, user.Id);
    }

    /// <summary>
    /// The user's cumulative adjustment over the generator's baseline. The baseline is invariant under
    /// repeated edits: baseline = currentQuantity − (currentDelta ?? 0), so edit → re-edit → revert all
    /// compute against the generator's own number. A delta of zero stores null (no edit to carry).
    /// </summary>
    public static decimal? ComputeQuantityDelta(decimal newQuantity, decimal? currentQuantity, decimal? currentDelta)
    {
        var baseline = (currentQuantity ?? 0) - (currentDelta ?? 0);
        var delta = newQuantity - baseline;
        return delta == 0 ? null : delta;
    }

    private static ShoppingListDto ToListDto(ShoppingList list) => new(
        list.ShoppingListId,
        list.Name,
        list.IsFavorite,
        list.IsArchived,
        list.MealPlanId != null,
        list.Items.Select(ToItemDto).ToList());

    private static ShoppingListItemDto ToItemDto(ShoppingListItem i) => new(
        i.ItemId,
        i.Name,
        i.Quantity,
        i.Unit,
        i.Category,
        i.IsChecked,
        i.CheckedAt,
        i.SortOrder,
        i.AddedBy?.DisplayName,
        i.AddedBy?.Initials,
        i.AddedBy?.PictureUrl,
        i.Version);

    private sealed record UserContext(int HouseholdId, int UserId);

    public sealed record PatchItemRequest(
        bool? IsChecked,
        decimal? Quantity,
        string? Unit,
        string? Name,
        string? Category);

    public sealed record AddItemRequest(
        string Name,
        decimal? Quantity,
        string? Unit,
        string? Category);

    public sealed record CreateListRequest(string Name);
    public sealed record RenameListRequest(string Name);

    public sealed record GenerateRequest(
        DateOnly StartDate,
        DateOnly EndDate,
        string? Name);

    public sealed record SortOrderUpdate(int ItemId, int SortOrder, string Category);
    public sealed record UpdateSortOrdersRequest(List<SortOrderUpdate> Updates);

    public sealed record ShoppingListDto(
        int Id,
        string Name,
        bool IsFavorite,
        bool IsArchived,
        bool HasMealPlan,
        IReadOnlyList<ShoppingListItemDto> Items);

    public sealed record ShoppingListItemDto(
        int Id,
        string Name,
        decimal? Quantity,
        string? Unit,
        string Category,
        bool IsChecked,
        DateTime? CheckedAt,
        int SortOrder,
        string? AddedByName,
        string? AddedByInitials,
        string? AddedByPictureUrl,
        uint Version);

    public sealed record ShoppingListSummaryDto(
        int Id,
        string Name,
        bool IsFavorite,
        int ItemCount,
        int UncheckedCount);

    /// <summary>Past-lists browse row. CreatedAt is a full UTC instant; HasMealPlan gates regenerate.</summary>
    public sealed record ArchivedListSummaryDto(
        int Id,
        string Name,
        bool IsFavorite,
        int ItemCount,
        int UncheckedCount,
        DateTime CreatedAt,
        bool HasMealPlan);
}
