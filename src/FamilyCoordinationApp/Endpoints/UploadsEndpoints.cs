using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// <c>GET /uploads/{householdId}/{fileName}</c> — the authorization gate in front of household user content
/// (recipe / chore / room photos written by <see cref="ImageService"/>).
///
/// <para><b>Why this exists.</b> These files live under <c>wwwroot/</c>, so before this endpoint they were served
/// by the plain <see cref="Microsoft.AspNetCore.Builder.StaticFileExtensions.UseStaticFiles(Microsoft.AspNetCore.Builder.IApplicationBuilder)"/>
/// middleware, which runs BEFORE <c>UseAuthentication</c> — there was no auth on the file path at all, so reading
/// another household's photo (or any photo while logged out) was guessing a GUID, not crossing a boundary.
/// <c>Program.cs</c> now branches <c>/uploads</c> away from the static-file middleware so this endpoint owns
/// those paths.</para>
///
/// <para><b>The allow rules</b>, cheapest first — a request is served only if one matches:</para>
/// <list type="number">
///   <item>the caller's own household owns the directory (every chore/room photo and own-recipe image);</item>
///   <item>the two households are CONNECTED <b>and</b> the OWNING household has a recipe pointing at this exact
///     file — connection alone is not enough, because connections share RECIPES and a directory-wide grant
///     would also expose that household's chore and room photos.</item>
/// </list>
///
/// <para><b>No rule may key on a row the CALLER owns.</b> <c>Recipe.ImagePath</c> is unvalidated client input on
/// create and stays mutable on update, so any such rule is an attacker-authored predicate — a caller could point
/// one of their own rows at any path and self-grant a read. An earlier draft of this gate did exactly that (to
/// keep images working on recipes copied from a household later disconnected) and silently reinstated the
/// boundary this file exists to close. It was removed rather than patched: measured against production, it
/// matched zero rows, because copied recipes there carry external <c>https://</c> image URLs and every stored
/// <c>/uploads/</c> path belongs to the household that owns it. The forward-looking fix for that case is for the
/// copy to duplicate the FILE into the copying household's directory, not to widen this gate.</para>
///
/// <para>A denied request gets <b>404, not 403</b> (never confirm which household ids or file names exist) with a
/// non-empty body, per the house <c>/api</c> rule: an empty 4xx re-executes through the GET-only
/// <c>/not-found</c> page.</para>
///
/// <para><b>Invariant this endpoint depends on:</b> nothing else may serve <c>wwwroot/uploads</c>.
/// <c>MapStaticAssets()</c> in <c>Program.cs</c> is NOT inside the <c>UseWhen</c> branch, and it globs
/// <c>wwwroot</c> at BUILD time — so any file present under <c>wwwroot/uploads/</c> when the app is published
/// would become an anonymous literal endpoint that outranks this route. It is empty today (the Dockerfile
/// creates the directory after publish and prod bind-mounts it), and
/// <c>UploadsGateIntegrationTests.Anonymous_CannotReadAnUploadThatExists</c> pins it: that test writes a real
/// file under <c>wwwroot/uploads</c> and fails if ANY serving path returns it anonymously. Never commit a file
/// under <c>wwwroot/uploads/</c>.</para>
/// </summary>
public static class UploadsEndpoints
{
    /// <summary>
    /// Extension → media type for the files this app is willing to SERVE. Keyed by the same extension set
    /// <see cref="ImageService.AllowedExtensions"/> accepts on upload — <c>UploadsContentTypeTests</c> locks the
    /// two together so a new accepted upload type cannot become an unservable file.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

    public static void MapUploadsEndpoints(this WebApplication app)
    {
        // GET + HEAD: the static-file middleware answered both, and this endpoint replaces it.
        app.MapMethods("/uploads/{householdId:int}/{fileName}", ["GET", "HEAD"], ServeUpload)
            .RequireAuthorization()
            .WithName("ServeUpload");
    }

    private static async Task<IResult> ServeUpload(
        int householdId,
        string fileName,
        HttpContext http,
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdConnectionService connectionService,
        IWebHostEnvironment environment,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        // Non-empty body: a bare Results.Unauthorized() is an empty 4xx, which re-executes through the
        // GET-only /not-found page — the same house rule the denial path below already honours.
        if (user is null) return Unauthorized();

        // The route parameter cannot contain a '/', but reject the traversal shapes explicitly rather than
        // relying on that: this check is the one a future refactor is most likely to route around.
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/') ||
            fileName.Contains('\\'))
        {
            return NotFound();
        }

        var extension = Path.GetExtension(fileName);
        if (!ContentTypes.TryGetValue(extension, out var contentType)) return NotFound();

        if (!await CanReadHouseholdUploadsAsync(user, householdId, fileName, dbFactory, connectionService, ct))
        {
            return NotFound();
        }

        // Resolve and re-verify containment even though the pieces are already validated — the same
        // defence-in-depth the delete path uses, and the shared helper keeps the two definitions of
        // "inside this household's directory" from drifting. Resolution is guarded for the same reason
        // DeleteImageAsync guards it: fileName reaches Path.GetFullPath from the request line, and a path the
        // OS refuses to parse must be a 404, not a 500.
        string fullPath;
        var householdRoot = ImageService.UploadRootFor(environment.WebRootPath, householdId);
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(householdRoot, fileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NotFound();
        }

        if (!ImageService.IsWithinDirectory(householdRoot, fullPath) || !File.Exists(fullPath))
        {
            return NotFound();
        }

        // `private` is load-bearing now that the response is authorized per-caller: the static-file middleware
        // used to emit a cacheable public response, and a shared cache holding one of these would re-serve it
        // across households. The file name is a GUID and its bytes never change, so the BROWSER may hold it.
        http.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(fullPath, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// The two allow rules, cheapest first. Extracted as <c>internal</c> so the boundary is unit-testable
    /// without a WebApplicationFactory (the house pattern, mirroring <c>ChoresEndpoints.ApplyCapacityAsync</c>)
    /// — this is the decision an integration test would only reach through HTTP, and it is the one worth
    /// pinning directly.
    /// </summary>
    internal static async Task<bool> CanReadHouseholdUploadsAsync(
        UserContextResolver.UserContext user,
        int householdId,
        string fileName,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdConnectionService connectionService,
        CancellationToken ct)
    {
        // Rule 1 — the caller's own household owns the directory. Every chore/room photo and own-recipe image.
        if (user.HouseholdId == householdId) return true;

        // Rule 2 — the households are connected AND the OWNING household has a recipe pointing at this exact
        // file. Connection alone is deliberately NOT enough: household connections share RECIPES, so a
        // directory-wide grant would also hand over that household's chore and room photos, which the
        // connected-recipe API never exposes.
        //
        // Note which row authorizes this: one owned by householdId, referencing householdId's OWN directory
        // (storedPath is built from the REQUESTED household). A household can therefore only ever "share" its
        // own files. Keying on a row owned by the CALLER instead would be an attacker-authored predicate,
        // because Recipe.ImagePath is unvalidated client input on recipe write and stays mutable on update
        // (RecipesEndpoints -> RecipeService.UpdateRecipeAsync assigns it verbatim).
        if (!await connectionService.AreHouseholdsConnectedAsync(user.HouseholdId, householdId, ct)) return false;

        var storedPath = $"/uploads/{householdId}/{fileName}";
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        return await context.Recipes
            .AnyAsync(r => r.HouseholdId == householdId && r.ImagePath == storedPath, ct);
    }

    /// <summary>
    /// 404 for every denial — an existing-but-forbidden file and a missing file are indistinguishable to the
    /// caller. The body is non-empty on purpose (an empty 4xx re-executes through the GET-only
    /// <c>/not-found</c> page).
    /// </summary>
    private static IResult NotFound() =>
        Results.Json(new { message = "Image not found." }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>401 with a body, for the same reason <see cref="NotFound"/> carries one.</summary>
    private static IResult Unauthorized() =>
        Results.Json(new { message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
}
