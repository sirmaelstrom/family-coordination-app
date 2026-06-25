using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for Settings island A (Household settings, strangler — mirrors <see cref="RecipesEndpoints"/>):
/// an <c>/api/settings</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler resolving
/// the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via <see cref="UserContextResolver"/>.
/// Categories ride the existing <see cref="ICategoryService"/>; members go through the new
/// <see cref="IHouseholdMemberService"/> (the safety rules live there, server-enforced — review R-A2).
///
/// <para>Parity-first ⇒ versionless / last-write-wins. Not-found / rejection responses carry a NON-EMPTY body so
/// the app-global <c>UseStatusCodePagesWithReExecute</c> leaves them as clean 4xx (an empty 404 on a non-GET
/// surfaces as a 405). Add-member returns 200 + an outcome envelope for the benign cases and 409 ONLY for the
/// cross-household collision (review R-A1). LITERAL routes (<c>/categories/sort-order</c>) are registered BEFORE the
/// parameterized <c>/categories/{id}</c> routes so the matcher doesn't shadow them (the #53 route-order gotcha).</para>
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings")
            .RequireAuthorization()
            .DisableAntiforgery();

        // ── Categories ──────────────────────────────────────────────────────────
        var categories = group.MapGroup("/categories");
        categories.MapGet("/", ListCategories);
        categories.MapPost("/", CreateCategory);
        // ⚠ literal BEFORE the parameterized /{categoryId:int} PUT (route-order gotcha, #53).
        categories.MapPut("/sort-order", UpdateSortOrder);
        categories.MapGet("/{categoryId:int}/in-use", CategoryInUse);
        categories.MapPut("/{categoryId:int}", UpdateCategory);
        categories.MapDelete("/{categoryId:int}", DeleteCategory);
        categories.MapPost("/{categoryId:int}/restore", RestoreCategory);

        // ── Members ─────────────────────────────────────────────────────────────
        var members = group.MapGroup("/members");
        members.MapGet("/", ListMembers);
        members.MapPost("/", AddMember);
        members.MapPut("/{userId:int}", SetWhitelist);
        members.MapDelete("/{userId:int}", DeleteMember);

        return app;
    }

    // ─── Categories ───────────────────────────────────────────────────────────────

    private static async Task<IResult> ListCategories(
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var all = await categoryService.GetCategoriesAsync(user.HouseholdId, includeDeleted: true, ct);
        var active = all.Where(c => !c.IsDeleted).OrderBy(c => c.SortOrder).Select(ToDto).ToList();
        var deleted = all.Where(c => c.IsDeleted).Select(ToDto).ToList();
        return Results.Ok(new CategoryListDto(active, deleted));
    }

    private static async Task<IResult> CreateCategory(
        CategoryWriteRequest req,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Category name is required." });
        }

        var created = await categoryService.CreateCategoryAsync(new Category
        {
            HouseholdId = user.HouseholdId,
            Name = req.Name.Trim(),
            IconEmoji = req.IconEmoji ?? string.Empty,
            Color = string.IsNullOrWhiteSpace(req.Color) ? "#808080" : req.Color,
            IsDefault = false,
        }, ct);

        return Results.Created($"/api/settings/categories/{created.CategoryId}", ToDto(created));
    }

    private static async Task<IResult> UpdateCategory(
        int categoryId,
        CategoryWriteRequest req,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Category name is required." });
        }

        var existing = await categoryService.GetCategoryAsync(user.HouseholdId, categoryId, ct);
        if (existing is null) return Results.NotFound(new { message = "Category not found." });

        existing.Name = req.Name.Trim();
        existing.IconEmoji = req.IconEmoji ?? string.Empty;
        existing.Color = string.IsNullOrWhiteSpace(req.Color) ? "#808080" : req.Color;
        var updated = await categoryService.UpdateCategoryAsync(existing, ct);
        return Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> DeleteCategory(
        int categoryId,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var existing = await categoryService.GetCategoryAsync(user.HouseholdId, categoryId, ct);
        if (existing is null) return Results.NotFound(new { message = "Category not found." });
        if (existing.IsDeleted) return Results.NoContent(); // already soft-deleted (idempotent)

        await categoryService.DeleteCategoryAsync(user.HouseholdId, categoryId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreCategory(
        int categoryId,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var existing = await categoryService.GetCategoryAsync(user.HouseholdId, categoryId, ct);
        if (existing is null) return Results.NotFound(new { message = "Category not found." });
        if (!existing.IsDeleted) return Results.NoContent(); // already active (idempotent)

        await categoryService.RestoreCategoryAsync(user.HouseholdId, categoryId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateSortOrder(
        SortOrderRequest req,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var orders = req.OrderedIds.Select((id, index) => (CategoryId: id, SortOrder: index)).ToList();
        await categoryService.UpdateSortOrderAsync(user.HouseholdId, orders, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CategoryInUse(
        int categoryId,
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var existing = await categoryService.GetCategoryAsync(user.HouseholdId, categoryId, ct);
        if (existing is null) return Results.NotFound(new { message = "Category not found." });

        var inUse = await categoryService.HasIngredientsAsync(user.HouseholdId, existing.Name, ct);
        return Results.Ok(new { inUse });
    }

    // ─── Members ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListMembers(
        ClaimsPrincipal principal,
        IHouseholdMemberService memberService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var members = await memberService.GetMembersAsync(user.HouseholdId, ct);
        return Results.Ok(new MemberListDto(user.UserId, members.Select(ToMemberDto).ToList()));
    }

    private static async Task<IResult> AddMember(
        AddMemberRequest req,
        ClaimsPrincipal principal,
        IHouseholdMemberService memberService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Email))
        {
            return Results.BadRequest(new { message = "Email is required." });
        }

        var result = await memberService.AddMemberAsync(user.HouseholdId, req.Email, ct);
        return result.Outcome switch
        {
            AddMemberOutcome.OtherHousehold => Results.Conflict(
                new { message = "This email is already associated with another household." }),
            AddMemberOutcome.AlreadyActive => Results.Ok(
                new MemberActionDto(ToMemberDto(result.User!), "alreadyActive")),
            AddMemberOutcome.Reenabled => Results.Ok(
                new MemberActionDto(ToMemberDto(result.User!), "reenabled")),
            _ => Results.Ok(new MemberActionDto(ToMemberDto(result.User!), "created")),
        };
    }

    private static async Task<IResult> SetWhitelist(
        int userId,
        SetWhitelistRequest req,
        ClaimsPrincipal principal,
        IHouseholdMemberService memberService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var (result, updated) = await memberService.SetWhitelistAsync(
            user.HouseholdId, user.UserId, userId, req.IsWhitelisted, ct);
        return result switch
        {
            MemberMutationResult.Ok => Results.Ok(ToMemberDto(updated!)),
            MemberMutationResult.SelfForbidden => Results.BadRequest(new { message = "You can't change your own access." }),
            MemberMutationResult.LastActiveForbidden => Results.Conflict(new { message = "Can't disable the last active member." }),
            _ => Results.NotFound(new { message = "Member not found." }),
        };
    }

    private static async Task<IResult> DeleteMember(
        int userId,
        ClaimsPrincipal principal,
        IHouseholdMemberService memberService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var result = await memberService.DeleteMemberAsync(user.HouseholdId, user.UserId, userId, ct);
        return result switch
        {
            MemberMutationResult.Ok => Results.NoContent(),
            MemberMutationResult.SelfForbidden => Results.BadRequest(new { message = "You can't delete yourself." }),
            MemberMutationResult.LastUserForbidden => Results.Conflict(new { message = "Can't delete the last user in the household." }),
            MemberMutationResult.Blocked => Results.Conflict(
                new { message = "Can't delete this member — they have activity history (e.g. completed chores). Disable them instead." }),
            _ => Results.NotFound(new { message = "Member not found." }),
        };
    }

    // ─── Projection ───────────────────────────────────────────────────────────────

    private static SettingsCategoryDto ToDto(Category c) => new(
        c.CategoryId,
        c.Name,
        string.IsNullOrEmpty(c.IconEmoji) ? null : c.IconEmoji,
        c.Color,
        c.IsDefault,
        c.SortOrder,
        c.DeletedAt);

    private static SettingsMemberDto ToMemberDto(User u) => new(
        u.Id,
        u.Email,
        string.IsNullOrEmpty(u.DisplayName) ? null : u.DisplayName,
        u.IsWhitelisted);
}

// ─── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CategoryWriteRequest(string Name, string? IconEmoji, string Color);
public sealed record SortOrderRequest(IReadOnlyList<int> OrderedIds);
public sealed record AddMemberRequest(string Email);
public sealed record SetWhitelistRequest(bool IsWhitelisted);
