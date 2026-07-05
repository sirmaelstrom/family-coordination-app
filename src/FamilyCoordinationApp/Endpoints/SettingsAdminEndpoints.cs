using System.Globalization;
using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for Settings island C (Admin, strangler — the heaviest, most auth-sensitive cluster).
/// Two groups under one file (review X2), both behind <c>.RequireAuthorization().DisableAntiforgery()</c>:
/// <list type="bullet">
/// <item><c>/api/settings/household-requests</c> — <b>SITE-ADMIN ONLY</b>. A per-handler 403 gate reads the
/// caller's email from claims and checks <see cref="ISiteAdminService"/> (3 routes don't justify a filter, R-C5).
/// These are GLOBAL reads/writes — no HouseholdId, gated by the 403, not by M1 (R-C8).</item>
/// <item><c>/api/settings/feedback</c> — <b>DUAL-MODE</b>. A site admin sees/acts on all households' feedback; a
/// regular user only their own household's (server-scoped, R-C1 — the circuit→REST lift would otherwise be an
/// IDOR). The <c>isSiteAdmin</c> flag rides in the list payload for the island's affordances.</item>
/// </list>
///
/// <para><b>Email plumbing (R-C5):</b> <see cref="UserContextResolver"/> intentionally drops the email (returns
/// only HouseholdId/UserId), so the site-admin check reads <c>ClaimTypes.Email</c> from the principal DIRECTLY,
/// and the resolver is called separately only for the household scope. <b>Non-empty 4xx (R-C5 / memory
/// fca-empty-404-surfaces-as-405-on-delete):</b> the 403 and the IDOR 404 both carry a JSON body, else the global
/// <c>UseStatusCodePagesWithReExecute</c> re-executes an empty non-GET 4xx as a 405.</para>
///
/// <para><b>Dates (X5):</b> RequestedAt/ReviewedAt/CreatedAt are full UTC instants → projected to explicit
/// ISO-8601 <c>Z</c> strings (<see cref="ToIso"/>) so the wire format is unambiguous and the island renders local
/// via <c>new Date(iso).toLocaleString()</c>.</para>
/// </summary>
public static class SettingsAdminEndpoints
{
    public static IEndpointRouteBuilder MapSettingsAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Household requests (site-admin only) ──────────────────────────────────
        var requests = app.MapGroup("/api/settings/household-requests")
            .RequireAuthorization()
            .DisableAntiforgery();
        requests.MapGet("/", GetHouseholdRequests);
        requests.MapPost("/{id:int}/approve", ApproveRequest);
        requests.MapPost("/{id:int}/reject", RejectRequest);

        // ── Households (site-admin only) — admin-initiated create (the "push" invite) ──
        var households = app.MapGroup("/api/settings/households")
            .RequireAuthorization()
            .DisableAntiforgery();
        households.MapPost("/", CreateHousehold);

        // ── Feedback (dual-mode) ──────────────────────────────────────────────────
        var feedback = app.MapGroup("/api/settings/feedback")
            .RequireAuthorization()
            .DisableAntiforgery();
        feedback.MapGet("/", GetFeedback);
        feedback.MapPost("/{id:int}/read", MarkFeedbackRead);
        feedback.MapPost("/{id:int}/resolve", MarkFeedbackResolved);
        feedback.MapPost("/{id:int}/reopen", ReopenFeedback);

        return app;
    }

    // ─── Household requests (site-admin only) ─────────────────────────────────────

    /// <summary>#1 GET / — every request (pending-first) + every household with member count (403 if not site-admin).</summary>
    private static async Task<IResult> GetHouseholdRequests(
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        if (RequireSiteAdmin(principal, siteAdmin) is { } forbidden) return forbidden;

        var data = await requestService.GetDataAsync(ct);
        return Results.Ok(new HouseholdRequestsDto(
            data.Requests.Select(ToRequestDto).ToList(),
            data.Households.Select(ToSummaryDto).ToList()));
    }

    /// <summary>#2 POST /{id}/approve — the atomic household-creation transaction (201). 403 / 404 / 409 (already reviewed, R-C3).</summary>
    private static async Task<IResult> ApproveRequest(
        int id,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        if (RequireSiteAdmin(principal, siteAdmin) is { } forbidden) return forbidden;

        var reviewerEmail = principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var result = await requestService.ApproveAsync(id, reviewerEmail, ct);
        return result.Outcome switch
        {
            ReviewOutcome.NotFound => Results.NotFound(new { message = "Household request not found." }),
            ReviewOutcome.AlreadyReviewed => Results.Conflict(new { message = "This request has already been reviewed." }),
            // Email already a user (stale data / concurrent approve) — a clean 409, not a 500 (council R1).
            ReviewOutcome.EmailInUse => Results.Conflict(new { message = "Could not approve — a user with that email already exists." }),
            // Member count is exactly 1 (the request's user, just created in the transaction).
            _ => Results.Created("/api/settings/household-requests", new HouseholdSummaryDto(
                result.CreatedHousehold!.Id, result.CreatedHousehold.Name, 1, ToIso(result.CreatedHousehold.CreatedAt))),
        };
    }

    /// <summary>#3 POST /{id}/reject — reason OPTIONAL (R-C7). 403 / 404 / 409 (already reviewed, R-C3) / else 204.</summary>
    private static async Task<IResult> RejectRequest(
        int id,
        RejectReasonRequest? req,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        if (RequireSiteAdmin(principal, siteAdmin) is { } forbidden) return forbidden;

        // Reason is optional (R-C7): a missing body / empty reason is fine. But guard the column's 500-char limit
        // server-side so an oversized direct-API reason is a clean 400, not a varchar-overflow 500 (council R4).
        if (req?.Reason is { Length: > 500 })
        {
            return Results.BadRequest(new { message = "Rejection reason must be 500 characters or fewer." });
        }

        var reviewerEmail = principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var outcome = await requestService.RejectAsync(id, req?.Reason, reviewerEmail, ct);
        return outcome switch
        {
            ReviewOutcome.NotFound => Results.NotFound(new { message = "Household request not found." }),
            ReviewOutcome.AlreadyReviewed => Results.Conflict(new { message = "This request has already been reviewed." }),
            _ => Results.NoContent(),
        };
    }

    /// <summary>
    /// #3b POST /api/settings/households — admin-initiated household create (the "push" invite mirroring the
    /// self-request→approve "pull"). Site-admin only. Validates + caps inputs to the column limits server-side so an
    /// oversized direct-API value is a clean 400, not a varchar-overflow 500 (parity RejectRequest's 500-char guard).
    /// 403 / 400 (blank or too long) / 409 (email already a member) / else 201 with the new household summary.
    /// </summary>
    private static async Task<IResult> CreateHousehold(
        CreateHouseholdRequest? req,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        if (RequireSiteAdmin(principal, siteAdmin) is { } forbidden) return forbidden;

        var name = req?.HouseholdName?.Trim() ?? "";
        var email = req?.OwnerEmail?.Trim() ?? "";
        if (name.Length == 0 || email.Length == 0)
        {
            return Results.BadRequest(new { message = "Household name and owner email are required." });
        }
        if (name.Length > 200)
        {
            return Results.BadRequest(new { message = "Household name must be 200 characters or fewer." });
        }
        if (email.Length > 256)
        {
            return Results.BadRequest(new { message = "Owner email must be 256 characters or fewer." });
        }
        if (req?.OwnerDisplayName is { Length: > 200 })
        {
            return Results.BadRequest(new { message = "Owner display name must be 200 characters or fewer." });
        }

        var createdBy = principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var result = await requestService.CreateHouseholdAsync(name, email, req?.OwnerDisplayName, createdBy, ct);
        return result.Outcome switch
        {
            CreateHouseholdOutcome.InvalidInput => Results.BadRequest(new { message = "Household name and owner email are required." }),
            CreateHouseholdOutcome.EmailInUse => Results.Conflict(new { message = "That email already belongs to a household member." }),
            // Member count is exactly 1 (the owner, just created in the transaction).
            _ => Results.Created("/api/settings/households", new HouseholdSummaryDto(
                result.Household!.Id, result.Household.Name, 1, ToIso(result.Household.CreatedAt))),
        };
    }

    // ─── Feedback (dual-mode) ─────────────────────────────────────────────────────

    /// <summary>#4 GET / — feedback for the caller (admin: all; regular: own household), + the isSiteAdmin signal.</summary>
    private static async Task<IResult> GetFeedback(
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IFeedbackService feedbackService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var (isSiteAdmin, householdId, authorized) = await ResolveFeedbackScopeAsync(principal, siteAdmin, dbFactory, ct);
        if (!authorized) return Results.Unauthorized();

        var items = await feedbackService.GetFeedbackAsync(isSiteAdmin, householdId, ct);
        return Results.Ok(new FeedbackListDto(isSiteAdmin, items.Select(ToFeedbackDto).ToList()));
    }

    /// <summary>#5 POST /{id}/read — scoped (R-C1): admin any; non-admin own-household only, else 404 (no leak).</summary>
    private static async Task<IResult> MarkFeedbackRead(
        int id, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        var (isSiteAdmin, householdId, authorized) = await ResolveFeedbackScopeAsync(principal, siteAdmin, dbFactory, ct);
        if (!authorized) return Results.Unauthorized();

        return await feedbackService.MarkReadAsync(id, isSiteAdmin, householdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    /// <summary>#6 POST /{id}/resolve — sets read+resolved (parity). Same scoping (R-C1).</summary>
    private static async Task<IResult> MarkFeedbackResolved(
        int id, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        var (isSiteAdmin, householdId, authorized) = await ResolveFeedbackScopeAsync(principal, siteAdmin, dbFactory, ct);
        if (!authorized) return Results.Unauthorized();

        return await feedbackService.MarkResolvedAsync(id, isSiteAdmin, householdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    /// <summary>#7 POST /{id}/reopen — clears resolved. Same scoping (R-C1).</summary>
    private static async Task<IResult> ReopenFeedback(
        int id, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        var (isSiteAdmin, householdId, authorized) = await ResolveFeedbackScopeAsync(principal, siteAdmin, dbFactory, ct);
        if (!authorized) return Results.Unauthorized();

        return await feedbackService.ReopenAsync(id, isSiteAdmin, householdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    // ─── Auth helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The site-admin 403 gate (R-C5). Reads the email from claims directly (the resolver drops it) and returns a
    /// NON-EMPTY-body 403 when the caller isn't a site admin, or null to proceed.
    /// </summary>
    private static IResult? RequireSiteAdmin(ClaimsPrincipal principal, ISiteAdminService siteAdmin)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        return siteAdmin.IsSiteAdmin(email)
            ? null
            : Results.Json(new { message = "Site admin access required." }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Resolve the feedback caller's scope (R-C5): isSiteAdmin from the claim email DIRECTLY + householdId from the
    /// resolver (for a non-admin's M1 scope). A site admin is authorized even without a resolved user row; a
    /// non-admin must resolve to a household (else 401).
    /// </summary>
    private static async Task<(bool IsSiteAdmin, int? HouseholdId, bool Authorized)> ResolveFeedbackScopeAsync(
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        var isSiteAdmin = siteAdmin.IsSiteAdmin(email);
        var caller = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        var authorized = isSiteAdmin || caller is not null;
        return (isSiteAdmin, caller?.HouseholdId, authorized);
    }

    private static IResult NotFoundFeedback() => Results.NotFound(new { message = "Feedback not found." });

    // ─── Projection ───────────────────────────────────────────────────────────────

    private static HouseholdRequestDto ToRequestDto(HouseholdRequest r) => new(
        r.Id,
        r.HouseholdName,
        r.DisplayName,
        r.Email,
        r.Status,
        ToIso(r.RequestedAt),
        r.ReviewedAt is { } reviewed ? ToIso(reviewed) : null,
        r.ReviewedBy,
        r.RejectionReason);

    private static HouseholdSummaryDto ToSummaryDto(Household h) => new(
        h.Id, h.Name, h.Users.Count, ToIso(h.CreatedAt));

    private static FeedbackDto ToFeedbackDto(Feedback f) => new(
        f.Id,
        f.Type,
        f.Message,
        string.IsNullOrEmpty(f.CurrentPage) ? null : f.CurrentPage,
        f.IsRead,
        f.IsResolved,
        ToIso(f.CreatedAt),
        // R-C6 author 3-way: live user → DisplayName; deleted user (UserId set, no row) → null+deleted; anon → null+not-deleted.
        f.User is not null ? f.User.DisplayName : null,
        f.User is null && f.UserId is not null);

    /// <summary>
    /// Format a stored UTC instant as an explicit ISO-8601 <c>Z</c> string (X5). The values are written as
    /// <c>DateTime.UtcNow</c>; <see cref="DateTime.SpecifyKind(DateTime, DateTimeKind)"/> asserts UTC without
    /// shifting (robust whether Npgsql returns Kind=Utc or Unspecified), so the island parses an unambiguous instant.
    /// </summary>
    private static string ToIso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

// ─── Request DTOs ───────────────────────────────────────────────────────────────

/// <summary>Reject body — <see cref="Reason"/> is OPTIONAL (nullable; empty allowed, R-C7).</summary>
public sealed record RejectReasonRequest(string? Reason);

/// <summary>
/// Admin create-household body: the household name + its owner's email (whitelisted so they drop straight in on
/// first Google login), plus an OPTIONAL display name (defaults to the email local-part). Server-validated.
/// </summary>
public sealed record CreateHouseholdRequest(string? HouseholdName, string? OwnerEmail, string? OwnerDisplayName);
