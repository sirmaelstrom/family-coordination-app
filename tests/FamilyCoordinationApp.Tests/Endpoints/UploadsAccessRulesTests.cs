using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Endpoints;

/// <summary>
/// The tenant boundary on <c>GET /uploads/{householdId}/{fileName}</c> (A5). Before this gate the files were
/// served by plain <c>UseStaticFiles()</c> ahead of <c>UseAuthentication()</c>, so there was no decision to
/// test at all; these pin the one that replaced it.
///
/// <para>The case that matters most here is <see cref="ConnectedHousehold_FileNotReferencedByAnyRecipe_IsDenied"/>
/// and the forged-row pair: an allow rule may only key on a row owned by the household that OWNS the
/// directory, because <c>Recipe.ImagePath</c> is unvalidated client input on create and stays mutable on
/// update — a rule keyed on a CALLER-owned row is authored by the attacker.</para>
/// </summary>
public class UploadsAccessRulesTests
{
    private static readonly UserContextResolver.UserContext Caller = new(HouseholdId: 1, UserId: 10);

    [Fact]
    public async Task Rule1_OwnHousehold_IsAllowed()
    {
        var (factory, _) = NewFactory();

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 1, "photo.jpg", factory, NotConnected(), CancellationToken.None);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Rule1_OwnHousehold_NeedsNoRecipeReference()
    {
        // Chore and room photos are never referenced by a recipe. Rule 1 must not require one.
        var (factory, _) = NewFactory();

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 1, "chore-photo.jpg", factory, NotConnected(), CancellationToken.None);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Rule2_ConnectedHousehold_FileReferencedByItsOwnRecipe_IsAllowed()
    {
        // Browsing a connected household's recipes must keep their images.
        var (factory, options) = NewFactory();
        SeedRecipe(options, householdId: 2, imagePath: "/uploads/2/photo.jpg");

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 2, "photo.jpg", factory, Connected(), CancellationToken.None);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectedHousehold_FileNotReferencedByAnyRecipe_IsDenied()
    {
        // Connection alone must NOT be a directory-wide grant: household connections share RECIPES, so a
        // bare "are they connected" rule would also hand over chore and room photos, which the connected
        // recipe API never exposes.
        var (factory, _) = NewFactory();

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 2, "chore-photo.jpg", factory, Connected(), CancellationToken.None);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnconnectedHousehold_IsDenied()
    {
        var (factory, options) = NewFactory();
        SeedRecipe(options, householdId: 2, imagePath: "/uploads/2/photo.jpg");

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 2, "photo.jpg", factory, NotConnected(), CancellationToken.None);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ForgedCallerOwnedRow_PointingAtAnotherHousehold_IsDenied()
    {
        // THE ATTACK. RecipeWriteRequest.ImagePath is unvalidated client input, so a caller can create a
        // recipe in their OWN household pointing at any path. If any rule matched on a caller-owned row, that
        // alone would grant a cross-tenant read and the gate would be self-defeating. Not connected here.
        var (factory, options) = NewFactory();
        SeedRecipe(options, householdId: 1, imagePath: "/uploads/2/photo.jpg", sharedFromHouseholdId: 2);

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 2, "photo.jpg", factory, NotConnected(), CancellationToken.None);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ForgedCallerOwnedRow_WhileConnected_StillDoesNotAuthorizeAnUnreferencedFile()
    {
        // The same forgery while a connection happens to exist. The authorizing row must belong to the
        // household that OWNS the directory; the caller's own row must never count, or a connected pair
        // collapses back to a directory-wide grant that survives via a caller-authored string.
        var (factory, options) = NewFactory();
        SeedRecipe(options, householdId: 1, imagePath: "/uploads/2/secret.jpg", sharedFromHouseholdId: 2);

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 2, "secret.jpg", factory, Connected(), CancellationToken.None);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Rule2_ReferenceMustNameTheOwningHouseholdsOwnDirectory()
    {
        // Household 2 cannot "share" household 3's file by pointing one of its recipes at it: storedPath is
        // built from the REQUESTED household, so a match requires 2's row to reference 2's own directory.
        var (factory, options) = NewFactory();
        SeedRecipe(options, householdId: 2, imagePath: "/uploads/3/photo.jpg");

        var allowed = await UploadsEndpoints.CanReadHouseholdUploadsAsync(
            Caller, householdId: 3, "photo.jpg", factory, Connected(), CancellationToken.None);

        allowed.Should().BeFalse();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static IHouseholdConnectionService Connected() => ConnectionService(true);

    private static IHouseholdConnectionService NotConnected() => ConnectionService(false);

    private static IHouseholdConnectionService ConnectionService(bool connected)
    {
        var mock = new Mock<IHouseholdConnectionService>();
        mock.Setup(s => s.AreHouseholdsConnectedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connected);
        return mock.Object;
    }

    private static (IDbContextFactory<ApplicationDbContext> Factory, DbContextOptions<ApplicationDbContext> Options)
        NewFactory()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));
        return (mock.Object, options);
    }

    private static void SeedRecipe(
        DbContextOptions<ApplicationDbContext> options,
        int householdId,
        string imagePath,
        int? sharedFromHouseholdId = null)
    {
        using var ctx = new ApplicationDbContext(options);
        if (!ctx.Households.Any(h => h.Id == householdId))
        {
            ctx.Households.Add(new Household { Id = householdId, Name = $"H{householdId}" });
        }
        ctx.Recipes.Add(new Recipe
        {
            HouseholdId = householdId,
            RecipeId = 1,
            Name = "Probe",
            ImagePath = imagePath,
            SharedFromHouseholdId = sharedFromHouseholdId
        });
        ctx.SaveChanges();
    }
}
