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

/// <summary>
/// Unit coverage for <see cref="HouseholdRequestService.CreateHouseholdAsync"/> — the admin-initiated "push" invite
/// (the mirror of the self-request→approve "pull"). InMemory EF (mirrors <see cref="HouseholdMemberServiceTests"/>);
/// the service opens a fresh context per call via the mocked factory over ONE in-memory DB, so state persists across
/// calls. The service wraps the create in a transaction — InMemory can't do transactions, so the options ignore
/// <see cref="InMemoryEventId.TransactionIgnoredWarning"/> (the transaction no-ops; the create logic still runs). The
/// true unique-<c>Users.Email</c> race → 409 path is a real-Postgres concern (left to the integration suite); the
/// pre-check covers the common case exercised here.
/// </summary>
public class HouseholdRequestServiceTests : IDisposable
{
    private const int ExistingHh = 1;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedContext;
    private readonly HouseholdRequestService _service;

    public HouseholdRequestServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _seedContext = new ApplicationDbContext(_options);
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));
        _service = new HouseholdRequestService(factory.Object, Mock.Of<ILogger<HouseholdRequestService>>());

        // One existing household + member, so the email-collision guard has something to collide with.
        _seedContext.Households.Add(new Household { Id = ExistingHh, Name = "The Firsts" });
        _seedContext.Users.Add(new User
        {
            Id = 1,
            HouseholdId = ExistingHh,
            Email = "alice@a.test",
            DisplayName = "Alice",
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow,
        });
        _seedContext.SaveChanges();
    }

    public void Dispose() => _seedContext.Dispose();

    private ApplicationDbContext NewContext() => new(_options);

    [Fact]
    public async Task CreateHousehold_NewOwner_CreatesHousehold_WithWhitelistedOwner_AndCategories()
    {
        var result = await _service.CreateHouseholdAsync("The Smiths", "bob@smith.test", "Bob Smith", "admin@site.test");

        result.Outcome.Should().Be(CreateHouseholdOutcome.Ok);
        result.Household.Should().NotBeNull();
        result.Household!.Name.Should().Be("The Smiths");

        await using var db = NewContext();
        var owner = await db.Users.SingleAsync(u => u.Email == "bob@smith.test");
        owner.HouseholdId.Should().Be(result.Household.Id);
        owner.DisplayName.Should().Be("Bob Smith");
        owner.IsWhitelisted.Should().BeTrue();
        owner.GoogleId.Should().BeNull(); // set on the owner's first Google login

        // Default categories were seeded for the NEW household inside the same operation.
        var categories = await db.Categories.Where(c => c.HouseholdId == result.Household.Id).ToListAsync();
        categories.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateHousehold_NoDisplayName_DefaultsToEmailLocalPart()
    {
        var result = await _service.CreateHouseholdAsync("Household X", "charlie@x.test", null, "admin@site.test");

        result.Outcome.Should().Be(CreateHouseholdOutcome.Ok);
        await using var db = NewContext();
        var owner = await db.Users.SingleAsync(u => u.Email == "charlie@x.test");
        owner.DisplayName.Should().Be("charlie");
    }

    [Fact]
    public async Task CreateHousehold_NormalizesEmail_TrimAndLowercase()
    {
        var result = await _service.CreateHouseholdAsync("Household Y", "  Dana@Y.TEST  ", "Dana", "admin@site.test");

        result.Outcome.Should().Be(CreateHouseholdOutcome.Ok);
        await using var db = NewContext();
        (await db.Users.AnyAsync(u => u.Email == "dana@y.test")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateHousehold_EmailAlreadyAMember_ReturnsEmailInUse_AndCreatesNoHousehold()
    {
        var before = await CountHouseholdsAsync();

        // Same email as the seeded member, in mixed case — the guard normalizes and rejects.
        var result = await _service.CreateHouseholdAsync("Dupe House", "ALICE@a.test", "Impostor", "admin@site.test");

        result.Outcome.Should().Be(CreateHouseholdOutcome.EmailInUse);
        result.Household.Should().BeNull();
        (await CountHouseholdsAsync()).Should().Be(before); // no orphan household
    }

    [Theory]
    [InlineData("", "owner@x.test")]
    [InlineData("   ", "owner@x.test")]
    [InlineData("Household", "")]
    [InlineData("Household", "   ")]
    public async Task CreateHousehold_BlankNameOrEmail_ReturnsInvalidInput(string name, string email)
    {
        var before = await CountHouseholdsAsync();

        var result = await _service.CreateHouseholdAsync(name, email, "Owner", "admin@site.test");

        result.Outcome.Should().Be(CreateHouseholdOutcome.InvalidInput);
        result.Household.Should().BeNull();
        (await CountHouseholdsAsync()).Should().Be(before);
    }

    private async Task<int> CountHouseholdsAsync()
    {
        await using var db = NewContext();
        return await db.Households.CountAsync();
    }
}
