using System.Net;
using FluentAssertions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end proof that <c>/uploads/*</c> is authorization-gated through the REAL middleware pipeline (A5).
///
/// <para>This is the test the change actually needs. The defect was a PIPELINE ORDERING bug —
/// <c>UseStaticFiles()</c> ran before <c>UseAuthentication()</c>, so household photos were served anonymously —
/// and no unit test can see pipeline ordering. <see cref="Anonymous_CannotReadAnUploadThatExists"/> is also the
/// regression pin for the second door: if <c>UseStaticFiles</c> OR <c>MapStaticAssets</c> ever serves
/// <c>wwwroot/uploads</c> again, the anonymous request returns 200 and this fails.</para>
///
/// <para>Files are written under the host's real <c>WebRootPath</c> and removed in <c>Dispose</c>. Nothing may
/// be committed under <c>wwwroot/uploads/</c> — a file present there at publish time becomes an anonymous
/// literal <c>MapStaticAssets</c> endpoint that outranks the gated route.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class UploadsGateIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private readonly List<string> _writtenFiles = [];
    private string _webRoot = string.Empty;

    private const string HouseholdAFile = "aaaaaaaa-0000-0000-0000-00000000000a.jpg";
    private const string HouseholdBFile = "bbbbbbbb-0000-0000-0000-00000000000b.jpg";
    private const string HouseholdBChorePhoto = "bbbbbbbb-0000-0000-0000-00000000000c.jpg";

    public async Task InitializeAsync()
    {
        await _factory.EnsureSeededAsync();

        _webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;

        WriteUpload(ChoresWebAppFactory.HouseholdAId, HouseholdAFile);
        WriteUpload(ChoresWebAppFactory.HouseholdBId, HouseholdBFile);
        WriteUpload(ChoresWebAppFactory.HouseholdBId, HouseholdBChorePhoto);
    }

    public async Task DisposeAsync()
    {
        foreach (var file in _writtenFiles)
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_CannotReadAnUploadThatExists()
    {
        // THE REGRESSION PIN. Before the gate this returned 200 with the image bytes — measured on a running
        // stack at 538,155 bytes. It must never be 200 again, by any serving path.
        using var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdAId}/{HouseholdAFile}");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OwnHouseholdUpload_IsServed_WithPrivateCaching()
    {
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdAId}/{HouseholdAFile}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        // Authorized per-caller: no shared cache may retain it.
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.Public.Should().BeFalse();
    }

    [Fact]
    public async Task CrossHouseholdUpload_IsNotFound_NotForbidden()
    {
        // 404, never 403 — a forbidden file and a missing one must be indistinguishable, or the response
        // confirms which household ids and file names exist.
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBFile}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty(
            "an empty 4xx re-executes through the GET-only /not-found page");
    }

    [Fact]
    public async Task ConnectedHousehold_ServesARecipeImage_ButNotAnUnreferencedPhoto()
    {
        // Connections share RECIPES. A connected caller may read household B's recipe image, but NOT B's
        // chore/room photos — a directory-wide grant would leak files the connected API never exposes.
        await ConnectHouseholdsAsync();
        await SetRecipeImageAsync(
            ChoresWebAppFactory.HouseholdBId, $"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBFile}");

        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var recipeImage = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBFile}");
        var chorePhoto = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBChorePhoto}");

        recipeImage.StatusCode.Should().Be(HttpStatusCode.OK);
        chorePhoto.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForgedOwnRecipeImagePath_DoesNotGrantCrossHouseholdRead()
    {
        // Recipe.ImagePath is unvalidated client input. Pointing one of household A's OWN recipes at
        // household B's file must not authorize reading it.
        await SetRecipeImageAsync(
            ChoresWebAppFactory.HouseholdAId, $"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBFile}");

        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdBId}/{HouseholdBFile}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_DenialCarriesABody()
    {
        // An empty 4xx re-executes through the GET-only /not-found page; the gate's own doc mandates a body.
        using var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdAId}/{HouseholdAFile}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Head_IsServed_LikeTheStaticFileMiddlewareUsedTo()
    {
        // MapMethods registers GET + HEAD: the middleware this endpoint replaced answered both.
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        using var request = new HttpRequestMessage(
            HttpMethod.Head, $"/uploads/{ChoresWebAppFactory.HouseholdAId}/{HouseholdAFile}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("..%2F..%2Fappsettings.json")]
    [InlineData("..%5C..%5Cappsettings.json")]
    [InlineData("%2e%2e%2fappsettings.json")]
    public async Task EncodedTraversalInTheFileNameSegment_NeverServesAFile(string encodedFileName)
    {
        // The route parameter cannot hold a raw '/', so these are the shapes that actually reach the handler.
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdAId}/{encodedFileName}");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NonImageExtension_IsRefused()
    {
        WriteUpload(ChoresWebAppFactory.HouseholdAId, "notes.txt");
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var response = await client.GetAsync($"/uploads/{ChoresWebAppFactory.HouseholdAId}/notes.txt");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private void WriteUpload(int householdId, string fileName)
    {
        var dir = Path.Combine(_webRoot, "uploads", householdId.ToString());
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllBytes(fullPath, [0xFF, 0xD8, 0xFF, 0xE0]); // JPEG magic; content is irrelevant to the gate
        _writtenFiles.Add(fullPath);
    }

    private async Task ConnectHouseholdsAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        var min = Math.Min(ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.HouseholdBId);
        var max = Math.Max(ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.HouseholdBId);
        if (!await context.HouseholdConnections.AnyAsync(c => c.HouseholdId1 == min && c.HouseholdId2 == max))
        {
            context.HouseholdConnections.Add(new HouseholdConnection
            {
                HouseholdId1 = min,
                HouseholdId2 = max,
                ConnectedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
    }

    private async Task SetRecipeImageAsync(int householdId, string imagePath)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        var recipe = await context.Recipes.FirstOrDefaultAsync(r => r.HouseholdId == householdId);
        if (recipe is null)
        {
            recipe = new Recipe
            {
                HouseholdId = householdId,
                RecipeId = 900 + householdId,
                Name = $"Gate probe {householdId}",
                CreatedAt = DateTime.UtcNow
            };
            context.Recipes.Add(recipe);
        }

        recipe.ImagePath = imagePath;
        await context.SaveChangesAsync();
    }
}
