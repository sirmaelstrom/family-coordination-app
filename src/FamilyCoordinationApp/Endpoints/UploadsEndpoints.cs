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
/// <c>Program.cs</c> now branches <c>/uploads</c> away from the static-file middleware so this is the only door.</para>
///
/// <para><b>The allow rules</b>, cheapest first — a request is served only if one matches:</para>
/// <list type="number">
///   <item>the caller's own household owns the directory (every chore/room photo and own-recipe image);</item>
///   <item>the two households are CONNECTED — the same predicate <c>/api/recipes/connected/{chId}</c> already
///     enforces for the recipe rows themselves, so browsing a connected household's recipes keeps its images;</item>
///   <item>the caller's own household holds a recipe row whose <c>ImagePath</c> IS this exact path. This is not a
///     cross-tenant read: <c>CopyRecipeFromConnectedHouseholdAsync</c> copies the source's <c>ImagePath</c>
///     verbatim, so a copied recipe permanently references the ORIGIN household's directory — without this rule,
///     disconnecting two households would silently break images on recipes the caller owns.</item>
/// </list>
///
/// <para>A denied request gets <b>404, not 403</b> (never confirm which household ids or file names exist) with a
/// non-empty body, per the house <c>/api</c> rule: an empty 4xx re-executes through the GET-only
/// <c>/not-found</c> page.</para>
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
        if (user is null) return Results.Unauthorized();

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
        // "inside this household's directory" from drifting.
        var householdRoot = ImageService.UploadRootFor(environment.WebRootPath, householdId);
        var fullPath = Path.GetFullPath(Path.Combine(householdRoot, fileName));
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

    /// <summary>The three allow rules, cheapest first. See the type doc for why rule 3 exists.</summary>
    private static async Task<bool> CanReadHouseholdUploadsAsync(
        UserContextResolver.UserContext user,
        int householdId,
        string fileName,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdConnectionService connectionService,
        CancellationToken ct)
    {
        if (user.HouseholdId == householdId) return true;

        if (await connectionService.AreHouseholdsConnectedAsync(user.HouseholdId, householdId, ct)) return true;

        // Rule 3 — a recipe THIS household owns points at the file. Matched on the stored URL form
        // (/uploads/{householdId}/{fileName}), which is exactly what SaveImageAsync returns and what the
        // copy path carries over.
        var storedPath = $"/uploads/{householdId}/{fileName}";
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        return await context.Recipes
            .AnyAsync(r => r.HouseholdId == user.HouseholdId && r.ImagePath == storedPath, ct);
    }

    /// <summary>
    /// 404 for every denial — an existing-but-forbidden file and a missing file are indistinguishable to the
    /// caller. The body is non-empty on purpose (an empty 4xx re-executes through the GET-only
    /// <c>/not-found</c> page).
    /// </summary>
    private static IResult NotFound() =>
        Results.Json(new { message = "Image not found." }, statusCode: StatusCodes.Status404NotFound);
}
