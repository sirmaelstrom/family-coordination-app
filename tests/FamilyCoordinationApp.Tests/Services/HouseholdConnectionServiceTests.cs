using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

public class HouseholdConnectionServiceTests : IDisposable
{
    private const string ValidCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactoryMock;
    private readonly Mock<ILogger<HouseholdConnectionService>> _connectionLoggerMock;
    private readonly Mock<ILogger<RecipeService>> _recipeLoggerMock;
    private Mock<IImageService> _imageServiceMock = null!;
    private readonly HouseholdConnectionService _connectionService;
    private readonly RecipeService _recipeService;

    public HouseholdConnectionServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new ApplicationDbContext(_options);
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _connectionLoggerMock = new Mock<ILogger<HouseholdConnectionService>>();
        _recipeLoggerMock = new Mock<ILogger<RecipeService>>();

        _connectionService = new HouseholdConnectionService(_dbFactoryMock.Object, _connectionLoggerMock.Object);
        _imageServiceMock = new Mock<IImageService>();
        _recipeService = new RecipeService(_dbFactoryMock.Object, _imageServiceMock.Object, _recipeLoggerMock.Object);

        // Clear rate limit state between tests
        HouseholdConnectionService.ClearRateLimitState();

        SeedTestData();
    }

    private void SeedTestData()
    {
        _context.Households.AddRange(
            new Household { Id = 1, Name = "Smith Family", CreatedAt = DateTime.UtcNow },
            new Household { Id = 2, Name = "Jones Family", CreatedAt = DateTime.UtcNow },
            new Household { Id = 3, Name = "Williams Family", CreatedAt = DateTime.UtcNow }
        );

        _context.Users.AddRange(
            new User { Id = 1, HouseholdId = 1, Email = "smith@test.com", DisplayName = "John Smith", Initials = "JS" },
            new User { Id = 2, HouseholdId = 2, Email = "jones@test.com", DisplayName = "Jane Jones", Initials = "JJ" },
            new User { Id = 3, HouseholdId = 3, Email = "williams@test.com", DisplayName = "Bob Williams", Initials = "BW" }
        );

        // Seed recipes for household 1 (source household for copy tests)
        _context.Recipes.Add(new Recipe
        {
            HouseholdId = 1,
            RecipeId = 1,
            Name = "Smith Chili",
            Description = "Family chili recipe",
            Instructions = "Cook the chili",
            Servings = 6,
            PrepTimeMinutes = 15,
            CookTimeMinutes = 60,
            RecipeType = RecipeType.Main,
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow
        });

        _context.RecipeIngredients.AddRange(
            new RecipeIngredient { HouseholdId = 1, RecipeId = 1, IngredientId = 1, Name = "Ground Beef", Quantity = 1, Unit = "lb", SortOrder = 1 },
            new RecipeIngredient { HouseholdId = 1, RecipeId = 1, IngredientId = 2, Name = "Kidney Beans", Quantity = 2, Unit = "cans", SortOrder = 2 },
            new RecipeIngredient { HouseholdId = 1, RecipeId = 1, IngredientId = 3, Name = "Tomato Sauce", Quantity = 1, Unit = "can", SortOrder = 3 }
        );

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        HouseholdConnectionService.ClearRateLimitState();
    }

    // --- GenerateInvite Tests ---

    [Fact]
    public async Task GenerateInvite_CreatesValidCode_WithCorrectCharset()
    {
        // Act
        var invite = await _connectionService.GenerateInviteAsync(1, 1);

        // Assert
        invite.Should().NotBeNull();
        invite.InviteCode.Should().HaveLength(6);
        invite.InviteCode.Should().MatchRegex($"^[{ValidCharset}]+$");
        invite.HouseholdId.Should().Be(1);
        invite.CreatedByUserId.Should().Be(1);
        invite.IsUsed.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateInvite_InvalidatesExistingInvites()
    {
        // Arrange - create an initial invite
        var firstInvite = await _connectionService.GenerateInviteAsync(1, 1);

        // Act - generate a new invite for the same household
        var secondInvite = await _connectionService.GenerateInviteAsync(1, 1);

        // Assert - first invite should be invalidated
        await using var verifyContext = new ApplicationDbContext(_options);
        var oldInvite = await verifyContext.HouseholdInvites
            .FirstOrDefaultAsync(i => i.Id == firstInvite.Id);
        oldInvite!.IsUsed.Should().BeTrue("existing invite should be invalidated when new one is generated");

        var newInvite = await verifyContext.HouseholdInvites
            .FirstOrDefaultAsync(i => i.Id == secondInvite.Id);
        newInvite!.IsUsed.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateInvite_DefaultExpiry_Is48Hours()
    {
        // Act
        var beforeGenerate = DateTime.UtcNow;
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        var afterGenerate = DateTime.UtcNow;

        // Assert — expiry should be ~48 hours from creation
        var expectedEarliestExpiry = beforeGenerate.AddHours(48);
        var expectedLatestExpiry = afterGenerate.AddHours(48);

        invite.ExpiresAt.Should().BeOnOrAfter(expectedEarliestExpiry.AddSeconds(-1));
        invite.ExpiresAt.Should().BeOnOrBefore(expectedLatestExpiry.AddSeconds(1));
    }

    // --- ValidateInviteCode Tests ---

    [Fact]
    public async Task ValidateCode_RejectsSelfConnection()
    {
        // Arrange
        var invite = await _connectionService.GenerateInviteAsync(1, 1);

        // Act — household 1 tries to use its own invite
        var (isValid, householdName, error) = await _connectionService.ValidateInviteCodeAsync(invite.InviteCode, 1);

        // Assert
        isValid.Should().BeFalse();
        error.Should().Be("You cannot connect to your own household.");
    }

    [Fact]
    public async Task ValidateCode_RejectsAlreadyConnected()
    {
        // Arrange — connect households 1 and 2
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Generate a new invite from household 1
        var newInvite = await _connectionService.GenerateInviteAsync(1, 1);

        // Act — household 2 tries to connect again
        var (isValid, _, error) = await _connectionService.ValidateInviteCodeAsync(newInvite.InviteCode, 2);

        // Assert
        isValid.Should().BeFalse();
        error.Should().Be("Your households are already connected.");
    }

    [Fact]
    public async Task ValidateCode_RejectsExpiredCode()
    {
        // Arrange — create an invite that's already expired
        await using var setupContext = new ApplicationDbContext(_options);
        var expiredInvite = new HouseholdInvite
        {
            HouseholdId = 1,
            InviteCode = "EXPRD1",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-49),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            IsUsed = false
        };
        setupContext.HouseholdInvites.Add(expiredInvite);
        await setupContext.SaveChangesAsync();

        // Act
        var (isValid, _, error) = await _connectionService.ValidateInviteCodeAsync("EXPRD1", 2);

        // Assert
        isValid.Should().BeFalse();
        error.Should().Be("This invite code has expired.");
    }

    [Fact]
    public async Task ValidateCode_RejectsUsedCode()
    {
        // Arrange — create a used invite
        await using var setupContext = new ApplicationDbContext(_options);
        var usedInvite = new HouseholdInvite
        {
            HouseholdId = 1,
            InviteCode = "USED01",
            CreatedByUserId = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddHours(47),
            IsUsed = true,
            UsedAt = DateTime.UtcNow.AddMinutes(-30),
            UsedByHouseholdId = 3,
            UsedByUserId = 3
        };
        setupContext.HouseholdInvites.Add(usedInvite);
        await setupContext.SaveChangesAsync();

        // Act
        var (isValid, _, error) = await _connectionService.ValidateInviteCodeAsync("USED01", 2);

        // Assert
        isValid.Should().BeFalse();
        error.Should().Be("This invite code has already been used.");
    }

    [Fact]
    public async Task ValidateCode_RejectsInvalidCode()
    {
        // Act
        var (isValid, _, error) = await _connectionService.ValidateInviteCodeAsync("XXXXXX", 2);

        // Assert
        isValid.Should().BeFalse();
        error.Should().Be("Invalid invite code.");
    }

    // --- AcceptInvite Tests ---

    [Fact]
    public async Task AcceptInvite_CreatesConnection_WithOrderedIds()
    {
        // Arrange — household 2 invites, household 1 accepts (higher invites lower)
        var invite = await _connectionService.GenerateInviteAsync(2, 2);

        // Act
        var (success, _, _) = await _connectionService.AcceptInviteAsync(invite.InviteCode, 1, 1);

        // Assert
        success.Should().BeTrue();

        await using var verifyContext = new ApplicationDbContext(_options);
        var connection = await verifyContext.HouseholdConnections.FirstOrDefaultAsync();
        connection.Should().NotBeNull();
        connection!.HouseholdId1.Should().Be(1, "smaller ID should always be HouseholdId1");
        connection.HouseholdId2.Should().Be(2, "larger ID should always be HouseholdId2");
    }

    [Fact]
    public async Task AcceptInvite_MarksInviteUsed()
    {
        // Arrange
        var invite = await _connectionService.GenerateInviteAsync(1, 1);

        // Act
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Assert
        await using var verifyContext = new ApplicationDbContext(_options);
        var usedInvite = await verifyContext.HouseholdInvites
            .FirstOrDefaultAsync(i => i.InviteCode == invite.InviteCode);
        usedInvite!.IsUsed.Should().BeTrue();
        usedInvite.UsedAt.Should().NotBeNull();
        usedInvite.UsedByHouseholdId.Should().Be(2);
        usedInvite.UsedByUserId.Should().Be(2);
    }

    [Fact]
    public async Task AcceptInvite_ReturnsConnectedHouseholdName()
    {
        // Arrange
        var invite = await _connectionService.GenerateInviteAsync(1, 1);

        // Act
        var (success, connectedName, error) = await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Assert
        success.Should().BeTrue();
        connectedName.Should().Be("Smith Family");
        error.Should().BeNull();
    }

    // --- GetConnectedHouseholds Tests ---

    [Fact]
    public async Task GetConnectedHouseholds_FindsFromBothSides()
    {
        // Arrange — connect households 1 and 2
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Act — query from both sides
        var fromHousehold1 = await _connectionService.GetConnectedHouseholdsAsync(1);
        var fromHousehold2 = await _connectionService.GetConnectedHouseholdsAsync(2);

        // Assert
        fromHousehold1.Should().HaveCount(1);
        fromHousehold1[0].HouseholdId.Should().Be(2);
        fromHousehold1[0].HouseholdName.Should().Be("Jones Family");

        fromHousehold2.Should().HaveCount(1);
        fromHousehold2[0].HouseholdId.Should().Be(1);
        fromHousehold2[0].HouseholdName.Should().Be("Smith Family");
    }

    // --- AreHouseholdsConnected Tests ---

    [Fact]
    public async Task AreConnected_ReturnsTrueRegardlessOfIdOrder()
    {
        // Arrange — connect households 1 and 3
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 3, 3);

        // Act — check both orderings
        var forward = await _connectionService.AreHouseholdsConnectedAsync(1, 3);
        var reverse = await _connectionService.AreHouseholdsConnectedAsync(3, 1);

        // Assert
        forward.Should().BeTrue();
        reverse.Should().BeTrue();
    }

    // --- Disconnect Tests ---

    [Fact]
    public async Task Disconnect_RemovesConnection()
    {
        // Arrange — connect then disconnect
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);
        (await _connectionService.AreHouseholdsConnectedAsync(1, 2)).Should().BeTrue();

        // Act
        await _connectionService.DisconnectHouseholdsAsync(1, 2);

        // Assert
        (await _connectionService.AreHouseholdsConnectedAsync(1, 2)).Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_PreservesCopiedRecipes()
    {
        // Arrange — connect, copy a recipe, then disconnect
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Copy a recipe from household 1 to household 2
        var copiedRecipe = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        // Act — disconnect the households
        await _connectionService.DisconnectHouseholdsAsync(1, 2);

        // Assert — the copied recipe should still exist with attribution intact
        await using var verifyContext = new ApplicationDbContext(_options);
        var recipe = await verifyContext.Recipes
            .FirstOrDefaultAsync(r => r.HouseholdId == 2 && r.RecipeId == copiedRecipe.RecipeId);
        recipe.Should().NotBeNull();
        recipe!.SharedFromHouseholdName.Should().Be("Smith Family", "denormalized name survives disconnect");
        recipe.SharedFromRecipeId.Should().Be(1);
    }

    // --- Recipe Privacy Tests ---

    [Fact]
    public async Task GetRecipesFromConnected_ExcludesCreatedByUser()
    {
        // Arrange — connect households 1 and 2
        var invite = await _connectionService.GenerateInviteAsync(1, 1);
        await _connectionService.AcceptInviteAsync(invite.InviteCode, 2, 2);

        // Act — household 2 browses household 1's recipes
        var recipes = await _recipeService.GetRecipesFromConnectedHouseholdAsync(2, 1);

        // Assert
        recipes.Should().HaveCount(1);
        recipes[0].Name.Should().Be("Smith Chili");
        recipes[0].CreatedBy.Should().BeNull("connected household should NOT see individual user details");
    }

    // --- CopyRecipe Tests ---

    [Fact]
    public async Task CopyRecipe_DuplicatesAStoredImageIntoTheCopyingHousehold()
    {
        // The source recipe carries its own household's stored upload; the copy must own a NEW file
        // in household 2's directory — never a permanent reference into household 1's (quest b0edfd94).
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var source = await ctx.Recipes.SingleAsync(r => r.HouseholdId == 1 && r.RecipeId == 1);
            source.ImagePath = "/uploads/1/chili.jpg";
            await ctx.SaveChangesAsync();
        }
        _imageServiceMock
            .Setup(s => s.CopyImageAsync("/uploads/1/chili.jpg", 1, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/uploads/2/new-guid.jpg");

        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        copied.ImagePath.Should().Be("/uploads/2/new-guid.jpg");
    }

    [Fact]
    public async Task CopyRecipe_DropsTheImageWhenTheSourceFileIsGone()
    {
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var source = await ctx.Recipes.SingleAsync(r => r.HouseholdId == 1 && r.RecipeId == 1);
            source.ImagePath = "/uploads/1/vanished.jpg";
            await ctx.SaveChangesAsync();
        }
        _imageServiceMock
            .Setup(s => s.CopyImageAsync(It.IsAny<string>(), 1, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        copied.ImagePath.Should().BeNull("a dangling cross-household reference is worse than no image");
    }

    [Fact]
    public async Task CopyRecipe_DropsALegacyPathThatIsNeitherASourceUploadNorHttp()
    {
        // A pre-policy row can point anywhere — a THIRD household's directory, a file: URL. Copying such a
        // value verbatim would recreate the cross-household reference this quest exists to end (slim-review
        // finding on PR #105).
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var source = await ctx.Recipes.SingleAsync(r => r.HouseholdId == 1 && r.RecipeId == 1);
            source.ImagePath = "/uploads/999/not-ours.jpg";
            await ctx.SaveChangesAsync();
        }

        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        copied.ImagePath.Should().BeNull();
        _imageServiceMock.Verify(
            s => s.CopyImageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "a path outside the source household's own directory must never be offered to the copier");
    }

    [Fact]
    public async Task CopyRecipe_KeepsAnExternalUrlVerbatim()
    {
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var source = await ctx.Recipes.SingleAsync(r => r.HouseholdId == 1 && r.RecipeId == 1);
            source.ImagePath = "https://example.test/chili.jpg";
            await ctx.SaveChangesAsync();
        }

        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        copied.ImagePath.Should().Be("https://example.test/chili.jpg");
        _imageServiceMock.Verify(
            s => s.CopyImageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "external URLs never touch the uploads directory");
    }

    [Fact]
    public async Task CopyRecipe_SetsAttribution()
    {
        // Act
        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        // Assert
        copied.SharedFromHouseholdId.Should().Be(1);
        copied.SharedFromHouseholdName.Should().Be("Smith Family");
        copied.SharedFromRecipeId.Should().Be(1);
        copied.HouseholdId.Should().Be(2);
        copied.CreatedByUserId.Should().Be(2);
        copied.Name.Should().Be("Smith Chili");
    }

    [Fact]
    public async Task CopyRecipe_DeepCopiesIngredients()
    {
        // Act
        var copied = await _recipeService.CopyRecipeFromConnectedHouseholdAsync(1, 1, 2, 2);

        // Assert — verify ingredients were deep copied
        await using var verifyContext = new ApplicationDbContext(_options);
        var copiedIngredients = await verifyContext.RecipeIngredients
            .Where(i => i.HouseholdId == 2 && i.RecipeId == copied.RecipeId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();

        copiedIngredients.Should().HaveCount(3);

        copiedIngredients[0].Name.Should().Be("Ground Beef");
        copiedIngredients[0].Quantity.Should().Be(1);
        copiedIngredients[0].Unit.Should().Be("lb");
        copiedIngredients[0].HouseholdId.Should().Be(2, "copied ingredient should belong to target household");

        copiedIngredients[1].Name.Should().Be("Kidney Beans");
        copiedIngredients[2].Name.Should().Be("Tomato Sauce");

        // Verify source ingredients are untouched
        var sourceIngredients = await verifyContext.RecipeIngredients
            .Where(i => i.HouseholdId == 1 && i.RecipeId == 1)
            .ToListAsync();
        sourceIngredients.Should().HaveCount(3, "source ingredients should remain unchanged");
    }
}
