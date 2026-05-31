using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for chores + the board (WP-06). Mirrors <c>ShoppingListEndpoints</c>: a
/// <c>/api/chores</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler
/// resolving the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via
/// <see cref="UserContextResolver"/>. Writes delegate to <see cref="IChoreService"/>; the board read +
/// the per-mutation response projection delegate to <see cref="IChoreBoardService"/> (ONE projection — no
/// card/mutation-response drift, M9). The service's typed exceptions map to HTTP status:
/// <see cref="ChoreConflictException"/> → 409 (xmin conflict, M7/M12),
/// <see cref="ChoreValidationException"/> → 400 (illegal transition / bad input, MN8),
/// <see cref="ChoreNotFoundException"/> → 404 (also covers cross-household access, M1).
/// </summary>
public static class ChoresEndpoints
{
    public static IEndpointRouteBuilder MapChoresEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chores")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapGet("/board", GetBoard);

        group.MapPost("/", CreateChore);
        group.MapPut("/{choreId:int}", UpdateChore);
        group.MapDelete("/{choreId:int}", DeleteChore);

        group.MapPost("/{choreId:int}/claim", ClaimChore);
        group.MapPost("/{choreId:int}/drop", DropChore);
        group.MapPost("/{choreId:int}/handoff", HandOffChore);
        group.MapPost("/{choreId:int}/complete", CompleteChore);
        group.MapPost("/{choreId:int}/photo", UploadChorePhoto);

        group.MapPatch("/me/default-view", SetDefaultView);

        // v1.1 (WP-06): equity distribution lens + digest settings + dev backfill — all cookie-authed,
        // household-scoped via UserContextResolver (M1).
        group.MapGet("/equity", GetEquity);
        group.MapGet("/digest-settings", GetDigestSettings);
        group.MapPut("/digest-settings", UpdateDigestSettings);
        group.MapPost("/seed-starter", SeedStarter);

        // v1.1 (WP-06, E8/E9): the cron-triggered digest run endpoint. Mapped on the TOP-LEVEL app — NOT on
        // `group` — because `group` carries `.RequireAuthorization()` (cookie auth) which would break the
        // shared-secret cron design (council). No RequireAuthorization; shared-secret token via header only
        // (MN10). Antiforgery disabled (no cookie/form).
        app.MapPost("/api/chores/digest/run", RunDigests).DisableAntiforgery();

        return app;
    }

    // ─── Board ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetBoard(
        ClaimsPrincipal principal,
        IChoreBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var board = await boardService.GetBoardAsync(user.HouseholdId, user.UserId, null, ct);
        return Results.Ok(board);
    }

    // ─── Chore CRUD ───────────────────────────────────────────────────────────────

    private static async Task<IResult> CreateChore(
        CreateChoreRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.CreateChoreAsync(user.HouseholdId, user.UserId, req.ToCommand(), ct);
            return Results.Created($"/api/chores/{chore.ChoreId}", Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> UpdateChore(
        int choreId,
        UpdateChoreRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.UpdateChoreAsync(user.HouseholdId, choreId, req.ToCommand(), req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> DeleteChore(
        int choreId,
        [FromBody] VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            await svc.DeleteChoreAsync(user.HouseholdId, choreId, req.Version, ct);
            return Results.NoContent();
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    // ─── Claim state machine ──────────────────────────────────────────────────────

    private static async Task<IResult> ClaimChore(
        int choreId,
        VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.ClaimAsync(user.HouseholdId, choreId, user.UserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> DropChore(
        int choreId,
        VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.DropAsync(user.HouseholdId, choreId, user.UserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> HandOffChore(
        int choreId,
        HandOffRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.HandOffAsync(user.HouseholdId, choreId, user.UserId, req.TargetUserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> CompleteChore(
        int choreId,
        CompleteRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.CompleteAsync(
                user.HouseholdId, choreId, user.UserId, req.Note, req.PhotoPath, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    // ─── Photo upload (dedicated multipart route, council C2) ───────────────────────

    private static async Task<IResult> UploadChorePhoto(
        int choreId,
        [FromForm] IFormFile file,
        ClaimsPrincipal principal,
        IImageService imageService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();
        if (file is null || file.Length == 0) return Results.BadRequest(new { message = "File is required" });

        try
        {
            var path = await imageService.SaveImageAsync(file, user.HouseholdId, ct);
            return Results.Ok(new { photoPath = path });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    // ─── Per-user default lens (D18, council M7 pinned route) ───────────────────────

    private static async Task<IResult> SetDefaultView(
        DefaultViewRequest req,
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var (outcome, normalized) = await ApplyDefaultViewAsync(dbFactory, user.UserId, req.View, ct);
        return outcome switch
        {
            DefaultViewOutcome.Ok => Results.Ok(new { view = normalized }),
            DefaultViewOutcome.InvalidLens => Results.BadRequest(
                new { message = $"Unknown lens id '{req.View}'. Valid: {string.Join(", ", ChoreLens.All)}" }),
            _ => Results.Unauthorized()
        };
    }

    internal enum DefaultViewOutcome { Ok, InvalidLens, UserMissing }

    /// <summary>
    /// Core of <see cref="SetDefaultView"/>, extracted so it is unit-testable without a WebApplicationFactory
    /// (council M10). null/blank clears the preference to the default (Needs-attention); a non-null value MUST
    /// be a canonical lens id (council M6 — anything else is rejected, never coerced, MN8); the write is scoped
    /// to <paramref name="userId"/> only (the resolved caller, M1).
    /// </summary>
    internal static async Task<(DefaultViewOutcome Outcome, string? Normalized)> ApplyDefaultViewAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        int userId,
        string? requestedView,
        CancellationToken ct)
    {
        string? view = string.IsNullOrWhiteSpace(requestedView) ? null : requestedView.Trim();
        if (view is not null && !ChoreLens.All.Contains(view))
        {
            return (DefaultViewOutcome.InvalidLens, null);
        }

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var entity = await context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (entity is null) return (DefaultViewOutcome.UserMissing, null);

        entity.ChoresDefaultView = view;
        await context.SaveChangesAsync(ct);

        return (DefaultViewOutcome.Ok, view);
    }

    // ─── Equity distribution lens (v1.1 WP-06) ──────────────────────────────────────

    /// <summary>
    /// GET /api/chores/equity?window=week|all — the household equity distribution (M1, household-scoped).
    /// Mirrors <see cref="ChoreBoardService"/>'s member/chore fetch; the per-member distribution comes from
    /// <see cref="ChoreEquityCalculator"/> (effort-weighted, Monday-start week), while fallingBehind/up-for-grabs
    /// counts are computed over active chores via <see cref="ChoreStatusCalculator"/> + the SHARED
    /// <see cref="ChoreAttention"/> predicates — the same predicates the digest uses, so the lens and the
    /// digest never diverge (council MAJOR). Unknown <c>window</c> → 400.
    /// </summary>
    private static async Task<IResult> GetEquity(
        [FromQuery] string? window,
        ClaimsPrincipal principal,
        ChoreEquityCalculator equityCalculator,
        ChoreStatusCalculator statusCalculator,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (!TryParseEquityWindow(window, out var equityWindow))
        {
            return Results.BadRequest(new { message = $"Unknown window '{window}'. Valid: week, all" });
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var members = await context.Users
            .Where(u => u.HouseholdId == user.HouseholdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl))
            .ToListAsync(ct);

        var completions = await context.ChoreCompletions
            .Where(c => c.HouseholdId == user.HouseholdId)
            .ToListAsync(ct);

        // Active chores only (mirror ChoreBoardService) for the attention counts.
        var activeChores = await context.Chores
            .Where(c => c.HouseholdId == user.HouseholdId && c.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        var equity = equityCalculator.Compute(completions, members, equityWindow, now, timeZone);

        var fallingBehindCount = 0;
        var upForGrabsCount = 0;
        foreach (var chore in activeChores)
        {
            var dueness = statusCalculator.Compute(ChoreRecurrenceSnapshot.FromChore(chore), now, timeZone);
            if (ChoreAttention.IsFallingBehind(dueness.DueState))
            {
                fallingBehindCount++;
            }

            var isClaimStale = statusCalculator.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now);
            if (ChoreAttention.IsUpForGrabs(chore.AssignmentKind, isClaimStale))
            {
                upForGrabsCount++;
            }
        }

        var dto = new ChoreEquityDto(
            Window: equityWindow == EquityWindow.Week ? "week" : "all",
            TotalPoints: equity.TotalPoints,
            TotalCompletions: equity.TotalCompletions,
            EqualSharePct: equity.EqualSharePct,
            FallingBehindCount: fallingBehindCount,
            UpForGrabsCount: upForGrabsCount,
            Members: equity.Members
                .Select(m => new MemberShareDto(
                    m.UserId, m.DisplayName, m.Initials, m.PictureUrl, m.Points, m.Completions, m.SharePct))
                .ToList());

        return Results.Ok(dto);
    }

    /// <summary>
    /// Equity window allowlist (M16). Default <c>week</c> when null/blank; case-insensitive
    /// <c>week</c>/<c>all</c>; anything else is rejected (→ 400). Extracted <c>internal static</c> so the
    /// allowlist is unit-testable without a WebApplicationFactory.
    /// </summary>
    internal static bool TryParseEquityWindow(string? window, out EquityWindow parsed)
    {
        if (string.IsNullOrWhiteSpace(window))
        {
            parsed = EquityWindow.Week;
            return true;
        }

        switch (window.Trim().ToLowerInvariant())
        {
            case "week":
                parsed = EquityWindow.Week;
                return true;
            case "all":
                parsed = EquityWindow.All;
                return true;
            default:
                parsed = EquityWindow.Week;
                return false;
        }
    }

    // ─── Digest settings (v1.1 WP-06) ────────────────────────────────────────────────

    /// <summary>
    /// GET /api/chores/digest-settings — the safe, household-scoped settings view (never the webhook URL, MN7).
    /// Enums serialize camelCase (cadence:"weekly", sendDayOfWeek:"sunday"…).
    /// </summary>
    private static async Task<IResult> GetDigestSettings(
        ClaimsPrincipal principal,
        IDigestSettingsService settingsService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var view = await settingsService.GetAsync(user.HouseholdId, ct);
        return Results.Ok(view);
    }

    /// <summary>
    /// PUT /api/chores/digest-settings — upsert the household's settings (household-scoped, M1).
    /// The webhook field is TRI-STATE on the wire (see <see cref="DigestSettingsRequest"/>): omitted ⇒ leave
    /// unchanged, non-blank ⇒ set/encrypt, explicit null/"" ⇒ clear. Validation → 400.
    /// </summary>
    private static async Task<IResult> UpdateDigestSettings(
        [FromBody] DigestSettingsRequest req,
        ClaimsPrincipal principal,
        IDigestSettingsService settingsService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            await settingsService.UpdateAsync(user.HouseholdId, req.ToUpdate(), ct);
            var view = await settingsService.GetAsync(user.HouseholdId, ct);
            return Results.Ok(view);
        }
        catch (DigestSettingsValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    // ─── Dev backfill (v1.1 WP-06, E15) ──────────────────────────────────────────────

    /// <summary>
    /// POST /api/chores/seed-starter — seed the caller's household with the starter rooms+chores set
    /// (household-scoped, M1). <see cref="SeedData.SeedChoresAndRoomsAsync"/> is internally idempotent and
    /// returns void, so this probes first to report whether anything was actually seeded.
    /// </summary>
    private static async Task<IResult> SeedStarter(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        bool alreadyHad;
        await using (var context = await dbFactory.CreateDbContextAsync(ct))
        {
            alreadyHad = await context.Rooms.AnyAsync(r => r.HouseholdId == user.HouseholdId, ct)
                || await context.Chores.AnyAsync(c => c.HouseholdId == user.HouseholdId, ct);
        }

        var seeded = !alreadyHad;
        await SeedData.SeedChoresAndRoomsAsync(dbFactory, user.HouseholdId);

        return Results.Ok(new { seeded });
    }

    // ─── Digest run trigger (v1.1 WP-06, E8/E9 — NO cookie auth; shared-secret token) ─

    /// <summary>
    /// POST /api/chores/digest/run — the cron-triggered digest run. NOT cookie-authed (it's mapped on the
    /// top-level app, not the auth group). Gated by a shared-secret token supplied ONLY via the
    /// <c>X-Digest-Trigger-Token</c> header (MN10 — query-string tokens rejected), fixed-time compared.
    /// Unconfigured → 503; missing/mismatched token → 401. Both error bodies are NON-EMPTY JSON so the
    /// app-global <c>UseStatusCodePagesWithReExecute</c> does not rewrite them into the Blazor page (v1.0
    /// WP-08 quirk). On success → <see cref="IDigestService.RunDueAsync"/> → 200 { sent, skipped, failed }.
    /// </summary>
    private static async Task<IResult> RunDigests(
        HttpContext httpContext,
        IConfiguration configuration,
        IDigestService digestService,
        CancellationToken ct)
    {
        var configuredToken = configuration["CHORES_DIGEST_TRIGGER_TOKEN"];
        var presentedToken = httpContext.Request.Headers["X-Digest-Trigger-Token"].ToString();

        if (!ValidateTriggerToken(configuredToken, presentedToken))
        {
            // Distinguish "feature off" (503) from "bad/missing token" (401), both with non-empty bodies.
            return string.IsNullOrEmpty(configuredToken)
                ? Results.Json(new { error = "digest trigger disabled" }, statusCode: 503)
                : Results.Json(new { error = "unauthorized" }, statusCode: 401);
        }

        var summary = await digestService.RunDueAsync(null, ct);
        return Results.Ok(new
        {
            sent = summary.Sent,
            skipped = summary.Skipped,
            failed = summary.Failed
        });
    }

    /// <summary>
    /// Shared-secret token check for the digest run endpoint (M9). Extracted <c>internal static</c> so it is
    /// unit-testable without a WebApplicationFactory. Refuses when the token is unconfigured (refuse-if-
    /// unconfigured); otherwise requires a non-empty presented token equal to the configured one under a
    /// constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>) comparison over UTF-8 bytes.
    /// The caller is responsible for only ever sourcing <paramref name="presentedToken"/> from the request
    /// header, never the query string (MN10).
    /// </summary>
    internal static bool ValidateTriggerToken(string? configuredToken, string? presentedToken)
    {
        if (string.IsNullOrEmpty(configuredToken)) return false;
        if (string.IsNullOrEmpty(presentedToken)) return false;

        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
        return CryptographicOperations.FixedTimeEquals(configuredBytes, presentedBytes);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static ChoreDto Project(
        IChoreBoardService boardService, Chore chore, TimeProvider timeProvider, TimeZoneInfo timeZone) =>
        boardService.ProjectChore(chore, timeProvider.GetUtcNow().UtcDateTime, timeZone);

    // ─── Request DTOs ───────────────────────────────────────────────────────────────

    /// <summary>The client's xmin token for an optimistic-concurrency-checked mutation (M7/M12).</summary>
    public sealed record VersionRequest(uint Version);

    public sealed record HandOffRequest(int? TargetUserId, uint Version);

    public sealed record CompleteRequest(string? Note, string? PhotoPath, uint Version);

    public sealed record DefaultViewRequest(string? View);

    /// <summary>
    /// PUT /digest-settings request body (v1.1 WP-06). Enums bind/serialize camelCase via the global
    /// <c>JsonStringEnumConverter(CamelCase)</c>: <c>cadence:"weekly"</c>, <c>sendDayOfWeek:"sunday"|…|"saturday"</c>.
    /// <para>
    /// <b>Tri-state webhook (frozen contract — WP-11 mirrors this exactly):</b> the <c>webhookUrl</c> property
    /// is nullable and defaults to <c>null</c>, but JSON cannot distinguish "absent" from "explicit null", so
    /// an explicit <c>webhookAction</c> discriminator carries the intent:
    /// <list type="bullet">
    ///   <item><description><c>webhookAction:"keep"</c> (or omitted/unknown) ⇒ leave the stored webhook
    ///   unchanged (<c>WebhookProvided = false</c>).</description></item>
    ///   <item><description><c>webhookAction:"set"</c> ⇒ replace/encrypt with <c>webhookUrl</c>
    ///   (<c>WebhookProvided = true</c>, value = the supplied string; a blank string is treated as a clear by
    ///   the service).</description></item>
    ///   <item><description><c>webhookAction:"clear"</c> ⇒ clear the stored webhook
    ///   (<c>WebhookProvided = true, WebhookUrl = null</c>).</description></item>
    /// </list>
    /// </summary>
    public sealed record DigestSettingsRequest(
        bool Enabled,
        DigestCadence Cadence,
        DayOfWeek SendDayOfWeek,
        int SendHourLocal,
        string? WebhookAction = null,
        string? WebhookUrl = null)
    {
        public DigestSettingsUpdate ToUpdate()
        {
            var action = WebhookAction?.Trim().ToLowerInvariant();
            return action switch
            {
                "set" => new DigestSettingsUpdate(
                    Enabled, Cadence, SendDayOfWeek, SendHourLocal,
                    WebhookProvided: true, WebhookUrl: WebhookUrl),
                "clear" => new DigestSettingsUpdate(
                    Enabled, Cadence, SendDayOfWeek, SendHourLocal,
                    WebhookProvided: true, WebhookUrl: null),
                // "keep", null, or anything unknown ⇒ leave the stored webhook untouched.
                _ => new DigestSettingsUpdate(
                    Enabled, Cadence, SendDayOfWeek, SendHourLocal,
                    WebhookProvided: false, WebhookUrl: null),
            };
        }
    }

    public sealed record CreateChoreRequest(
        string Name,
        string? Description,
        int? RoomId,
        RecurrenceMode RecurrenceMode,
        int? IntervalDays,
        DateOnly? AnchorDate,
        ChoreDaysOfWeek? DaysOfWeek,
        int? DayOfMonth,
        EffortTier EffortTier,
        int? OwnerUserId,
        int? AssigneeUserId,
        string? PhotoPath,
        string? Icon = null)
    {
        public CreateChoreCommand ToCommand() => new(
            Name,
            Description,
            RoomId,
            RecurrenceMode,
            IntervalDays,
            AnchorDate,
            DaysOfWeek,
            DayOfMonth,
            EffortTier,
            OwnerUserId,
            AssigneeUserId,
            PhotoPath,
            Icon ?? string.Empty);
    }

    public sealed record UpdateChoreRequest(
        string Name,
        string? Description,
        int? RoomId,
        RecurrenceMode RecurrenceMode,
        int? IntervalDays,
        DateOnly? AnchorDate,
        ChoreDaysOfWeek? DaysOfWeek,
        int? DayOfMonth,
        EffortTier EffortTier,
        int? OwnerUserId,
        string? PhotoPath,
        uint Version,
        string? Icon = null)
    {
        public UpdateChoreCommand ToCommand() => new(
            Name,
            Description,
            RoomId,
            RecurrenceMode,
            IntervalDays,
            AnchorDate,
            DaysOfWeek,
            DayOfMonth,
            EffortTier,
            OwnerUserId,
            PhotoPath,
            Icon ?? string.Empty);
    }
}
