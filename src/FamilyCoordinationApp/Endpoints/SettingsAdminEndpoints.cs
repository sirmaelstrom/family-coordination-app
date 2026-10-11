using System.Globalization;
using System.Security.Claims;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for Settings island C (Admin, strangler — the heaviest, most auth-sensitive cluster).
/// Three groups under one file (review X2), all with <c>.DisableAntiforgery()</c>:
/// <list type="bullet">
/// <item><c>/api/settings/household-requests</c> and <c>/api/settings/households</c> — <b>SITE-ADMIN ONLY</b>.
/// Gated by the <see cref="AuthorizationPolicies.SiteAdmin"/> policy (quest 82fc7f54), which refuses a non-admin with
/// a 403 before the handler or the request validation runs. These are GLOBAL reads/writes — no HouseholdId, gated by
/// the policy, not by M1 (R-C8).</item>
/// <item><c>/api/settings/feedback</c> — <b>DUAL-MODE</b>, behind the /api login gate. A site admin sees/acts on all
/// households' feedback; a regular user only their own household's (server-scoped, R-C1 — the circuit→REST lift
/// would otherwise be an IDOR). Site-admin here is a DATA-SCOPE decision (which rows), never an access gate: every
/// signed-in member may use these routes. The <c>isSiteAdmin</c> flag rides in the list payload for the island's
/// affordances. <b>POST /</b> is the SUBMIT route — any authenticated caller, not just an admin.</item>
/// </list>
///
/// <para><b>Email plumbing (R-C5):</b> <see cref="CallerScope"/> carries no email (only HouseholdId/UserId),
/// so the site-admin check reads <c>ClaimTypes.Email</c> from the principal (<see cref="SiteAdminPrincipalExtensions"/>),
/// and the feedback handlers take the <see cref="CallerScope"/> only for the household scope. <b>Non-empty 4xx
/// (R-C5 / memory fca-empty-404-surfaces-as-405-on-delete):</b> the policy's 403
/// (<see cref="ApiAuthorizationResultHandler"/>) and the IDOR 404 both carry a JSON body so callers get specific
/// detail instead of the generic /api backfill.</para>
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
            .RequireAuthorization(AuthorizationPolicies.SiteAdmin)
            .DisableAntiforgery();
        requests.MapGet("/", GetHouseholdRequests);
        requests.MapPost("/{id:int}/approve", ApproveRequest);
        requests.MapPost("/{id:int}/reject", RejectRequest);

        // ── Households (site-admin only) — admin-initiated create (the "push" invite) ──
        var households = app.MapGroup("/api/settings/households")
            .RequireAuthorization(AuthorizationPolicies.SiteAdmin)
            .DisableAntiforgery();
        households.MapPost("/", CreateHousehold);

        // ── Feedback (dual-mode) ──────────────────────────────────────────────────
        var feedback = app.MapGroup("/api/settings/feedback")
            // Login: the /api gate (the FallbackPolicy in Program.cs). Site-admin is a data scope here, not a gate.
            .RequireTenant()
            .DisableAntiforgery();
        feedback.MapPost("/", SubmitFeedback);
        feedback.MapGet("/", GetFeedback);
        feedback.MapPost("/{id:int}/read", MarkFeedbackRead);
        feedback.MapPost("/{id:int}/resolve", MarkFeedbackResolved);
        feedback.MapPost("/{id:int}/reopen", ReopenFeedback);

        return app;
    }

    // ─── Household requests (site-admin only) ─────────────────────────────────────

    /// <summary>#1 GET / — every request (pending-first) + every household with member count (403 if not site-admin).</summary>
    private static async Task<IResult> GetHouseholdRequests(
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        var data = await requestService.GetDataAsync(ct);
        return Results.Ok(new HouseholdRequestsDto(
            data.Requests.Select(ToRequestDto).ToList(),
            data.Households.Select(ToSummaryDto).ToList()));
    }

    /// <summary>#2 POST /{id}/approve — the atomic household-creation transaction (201). 403 / 404 / 409 (already reviewed, R-C3).</summary>
    private static async Task<IResult> ApproveRequest(
        int id,
        ClaimsPrincipal principal,
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
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
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        // Reason is optional (R-C7): a missing body / empty reason is fine. But guard the column's 500-char limit
        // server-side so an oversized direct-API reason is a clean 400, not a varchar-overflow 500 (council R4).
        // Hand-written, not an attribute (quest ec7a7331). The site-admin 403 now answers before either, from the
        // SiteAdmin policy in the authorization middleware (quest 82fc7f54).
        if (req?.Reason is { Length: > FieldLengths.HouseholdRequest.RejectionReason })
        {
            return Results.BadRequest(new { message = $"Rejection reason must be {FieldLengths.HouseholdRequest.RejectionReason} characters or fewer." });
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
        IHouseholdRequestService requestService,
        CancellationToken ct)
    {
        var name = req?.HouseholdName?.Trim() ?? "";
        var email = req?.OwnerEmail?.Trim() ?? "";
        if (name.Length == 0 || email.Length == 0)
        {
            return Results.BadRequest(new { message = "Household name and owner email are required." });
        }
        // Hand-written, not attributes: these limits apply to the TRIMMED values the service stores (quest ec7a7331).
        if (name.Length > FieldLengths.Household.Name)
        {
            return Results.BadRequest(new { message = $"Household name must be {FieldLengths.Household.Name} characters or fewer." });
        }
        if (email.Length > FieldLengths.User.Email)
        {
            return Results.BadRequest(new { message = $"Owner email must be {FieldLengths.User.Email} characters or fewer." });
        }
        if (req?.OwnerDisplayName is { Length: > FieldLengths.User.DisplayName })
        {
            return Results.BadRequest(new { message = $"Owner display name must be {FieldLengths.User.DisplayName} characters or fewer." });
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

    /// <summary>
    /// #3c POST / — submit feedback. Any authenticated caller, not just an admin.
    /// <para><b>Attribution (M1):</b> the body carries NO ids — user + household come from the caller's resolved
    /// context, so a caller cannot file into another household. <b>Type</b> is parsed from the wire string rather
    /// than model-bound, so an unknown value is OUR 400-with-body and not the binder's empty one (R-C5).</para>
    /// 401 (unresolvable caller) / 400 (blank or over-long message, unknown type) / else 204.
    /// </summary>
    private static async Task<IResult> SubmitFeedback(
        SubmitFeedbackRequest? req,
        HttpContext http,
        CallerScope caller,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IFeedbackService feedbackService,
        CancellationToken ct)
    {
        var scope = ResolveFeedbackScope(caller, principal, siteAdmin);

        var message = req?.Message?.Trim() ?? "";
        if (message.Length == 0)
        {
            return Results.BadRequest(new { message = "Feedback message is required." });
        }
        // Clean 400 rather than a varchar-overflow 500 (parity: RejectRequest's 500-char guard).
        // Hand-written, not an attribute: the limit applies to the TRIMMED message the service stores.
        if (message.Length > FieldLengths.Feedback.Message)
        {
            return Results.BadRequest(new { message = $"Feedback message must be {FieldLengths.Feedback.Message} characters or fewer." });
        }
        if (!TryParseFeedbackType(req?.Type, out var type))
        {
            return Results.BadRequest(new { message = "Feedback type must be one of: bug, featureRequest, general." });
        }

        await feedbackService.SubmitAsync(
            type,
            message,
            req?.CurrentPage,
            // Server-read: a client-supplied User-Agent is worthless as a diagnostic.
            http.Request.Headers.UserAgent.ToString(),
            scope.UserId,
            scope.HouseholdId,
            ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Wire value → enum against an EXPLICIT whitelist; null/empty ⇒ <see cref="FeedbackType.General"/> (R-C10).
    /// Not <c>Enum.TryParse</c>+<c>IsDefined</c>: that also accepts numeric strings and comma/flags forms
    /// (<c>"bug,general"</c> ⇒ <c>0|2</c> ⇒ <c>General</c>), which the wire contract rejects.
    /// </summary>
    private static bool TryParseFeedbackType(string? value, out FeedbackType type)
    {
        var wire = value?.Trim();
        switch (wire)
        {
            case null or "":
                type = FeedbackType.General;
                return true;
            case not null when wire.Equals("bug", StringComparison.OrdinalIgnoreCase):
                type = FeedbackType.Bug;
                return true;
            case not null when wire.Equals("featureRequest", StringComparison.OrdinalIgnoreCase):
                type = FeedbackType.FeatureRequest;
                return true;
            case not null when wire.Equals("general", StringComparison.OrdinalIgnoreCase):
                type = FeedbackType.General;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>#4 GET / — feedback for the caller (admin: all; regular: own household), + the isSiteAdmin signal.</summary>
    private static async Task<IResult> GetFeedback(
        CallerScope caller,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin,
        IFeedbackService feedbackService,
        CancellationToken ct)
    {
        var scope = ResolveFeedbackScope(caller, principal, siteAdmin);

        var items = await feedbackService.GetFeedbackAsync(scope.IsSiteAdmin, scope.HouseholdId, ct);
        return Results.Ok(new FeedbackListDto(scope.IsSiteAdmin, items.Select(ToFeedbackDto).ToList()));
    }

    /// <summary>#5 POST /{id}/read — scoped (R-C1): admin any; non-admin own-household only, else 404 (no leak).</summary>
    private static async Task<IResult> MarkFeedbackRead(
        int id, CallerScope caller, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, CancellationToken ct)
    {
        var scope = ResolveFeedbackScope(caller, principal, siteAdmin);

        return await feedbackService.MarkReadAsync(id, scope.IsSiteAdmin, scope.HouseholdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    /// <summary>#6 POST /{id}/resolve — sets read+resolved (parity). Same scoping (R-C1).</summary>
    private static async Task<IResult> MarkFeedbackResolved(
        int id, CallerScope caller, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, CancellationToken ct)
    {
        var scope = ResolveFeedbackScope(caller, principal, siteAdmin);

        return await feedbackService.MarkResolvedAsync(id, scope.IsSiteAdmin, scope.HouseholdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    /// <summary>#7 POST /{id}/reopen — clears resolved. Same scoping (R-C1).</summary>
    private static async Task<IResult> ReopenFeedback(
        int id, CallerScope caller, ClaimsPrincipal principal, ISiteAdminService siteAdmin,
        IFeedbackService feedbackService, CancellationToken ct)
    {
        var scope = ResolveFeedbackScope(caller, principal, siteAdmin);

        return await feedbackService.ReopenAsync(id, scope.IsSiteAdmin, scope.HouseholdId, ct)
            ? Results.NoContent()
            : NotFoundFeedback();
    }

    // ─── Data scope ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The feedback caller's DATA scope (R-C5), not an access gate: isSiteAdmin picks which rows the caller sees and
    /// may mark (all households vs. their own), and userId/householdId come from the <see cref="CallerScope"/> (a
    /// non-admin's M1 read scope, and the submit attribution). The feedback group is marked <c>RequireTenant()</c>, so
    /// an unresolvable caller has already had the middleware's 401 + JSON body.
    /// </summary>
    private static (bool IsSiteAdmin, int UserId, int HouseholdId) ResolveFeedbackScope(
        CallerScope caller,
        ClaimsPrincipal principal,
        ISiteAdminService siteAdmin) =>
        (siteAdmin.IsSiteAdmin(principal), caller.UserId, caller.HouseholdId);

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

/// <summary>
/// Submit body. Deliberately carries NO user/household/id fields — attribution comes from the caller's resolved
/// context (M1), so there is nothing here to point at another household. <see cref="Type"/> is the camelCase wire
/// value as a STRING (see <c>TryParseFeedbackType</c>); the User-Agent is read from headers, not from here.
/// </summary>
public sealed record SubmitFeedbackRequest(string? Type, string? Message, string? CurrentPage);
