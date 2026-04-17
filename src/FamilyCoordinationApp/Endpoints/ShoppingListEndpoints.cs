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
        group.MapGet("/{listId:int}", GetList);

        group.MapPatch("/{listId:int}/items/{itemId:int}", PatchItem);
        group.MapPost("/{listId:int}/items", AddItem);
        group.MapDelete("/{listId:int}/items/{itemId:int}", DeleteItem);
        group.MapPost("/{listId:int}/items/sort-orders", UpdateSortOrders);

        group.MapPost("/{listId:int}/actions/toggle-favorite", ToggleFavorite);
        group.MapPost("/{listId:int}/actions/archive", ArchiveList);
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

        var removed = await svc.ClearCheckedItemsAsync(ctx.HouseholdId, listId, ct);
        return Results.Ok(new { removed });
    }

    // ─── Items ────────────────────────────────────────────────────────────────

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
        if (req.Quantity is not null) item.Quantity = req.Quantity;
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
        if (list is null || list.IsArchived) return Results.NotFound();

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

    private static ShoppingListDto ToListDto(ShoppingList list) => new(
        list.ShoppingListId,
        list.Name,
        list.IsFavorite,
        list.IsArchived,
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
}
