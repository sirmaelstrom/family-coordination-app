using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit coverage for <see cref="HouseholdMemberService"/> — the safety rules lifted out of WhitelistAdmin.razor
/// (review R-A2). InMemory EF (mirrors <see cref="CategoryServiceTests"/>); the service opens a fresh context per
/// call via the mocked factory (same in-memory DB ⇒ state persists across calls). The DB-level FK ON DELETE SET
/// NULL behaviour (recipes survive a member delete) is schema, not service logic, so it is exercised by the
/// real-Postgres endpoint suite, not here.
/// </summary>
public class HouseholdMemberServiceTests : IDisposable
{
    private const int HhA = 1;
    private const int HhB = 2;
    private readonly ApplicationDbContext _seedContext;
    private readonly HouseholdMemberService _service;

    public HouseholdMemberServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _seedContext = new ApplicationDbContext(options);
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));
        _service = new HouseholdMemberService(factory.Object, Mock.Of<ILogger<HouseholdMemberService>>());

        Seed();
    }

    private void Seed()
    {
        _seedContext.Households.AddRange(
            new Household { Id = HhA, Name = "Household A" },
            new Household { Id = HhB, Name = "Household B" });
        _seedContext.Users.AddRange(
            new User { Id = 1, HouseholdId = HhA, Email = "alice@a.test", DisplayName = "Alice", IsWhitelisted = true, CreatedAt = DateTime.UtcNow },
            new User { Id = 2, HouseholdId = HhA, Email = "amy@a.test", DisplayName = "Amy", IsWhitelisted = true, CreatedAt = DateTime.UtcNow },
            new User { Id = 3, HouseholdId = HhA, Email = "dan@a.test", DisplayName = "Dan", IsWhitelisted = false, CreatedAt = DateTime.UtcNow },
            new User { Id = 9, HouseholdId = HhB, Email = "bob@b.test", DisplayName = "Bob", IsWhitelisted = true, CreatedAt = DateTime.UtcNow });
        _seedContext.SaveChanges();
    }

    public void Dispose() => _seedContext.Dispose();

    // ─── GetMembers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMembers_ReturnsOwnHouseholdOnly_OrderedByEmail()
    {
        var members = await _service.GetMembersAsync(HhA);
        members.Select(m => m.Email).Should().ContainInOrder("alice@a.test", "amy@a.test", "dan@a.test");
        members.Should().OnlyContain(m => m.HouseholdId == HhA);
    }

    // ─── AddMember ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddMember_NewEmail_Creates_WhitelistedWithLocalPartDisplayName()
    {
        var result = await _service.AddMemberAsync(HhA, "  NEWBIE@A.test ");
        result.Outcome.Should().Be(AddMemberOutcome.Created);
        result.User!.Email.Should().Be("newbie@a.test", "the email is trimmed + lowercased");
        result.User.DisplayName.Should().Be("newbie", "DisplayName is the email local-part (parity)");
        result.User.IsWhitelisted.Should().BeTrue();
        result.User.GoogleId.Should().BeNull();
    }

    [Fact]
    public async Task AddMember_ExistingDisabled_ReenablesThem()
    {
        var result = await _service.AddMemberAsync(HhA, "dan@a.test");
        result.Outcome.Should().Be(AddMemberOutcome.Reenabled);
        result.User!.IsWhitelisted.Should().BeTrue();

        var persisted = await _service.GetMembersAsync(HhA);
        persisted.Single(m => m.Email == "dan@a.test").IsWhitelisted.Should().BeTrue();
    }

    [Fact]
    public async Task AddMember_ExistingActive_IsNoOp()
    {
        var result = await _service.AddMemberAsync(HhA, "alice@a.test");
        result.Outcome.Should().Be(AddMemberOutcome.AlreadyActive);
    }

    [Fact]
    public async Task AddMember_EmailInAnotherHousehold_IsRejected_NoDataLeak()
    {
        var result = await _service.AddMemberAsync(HhA, "bob@b.test");
        result.Outcome.Should().Be(AddMemberOutcome.OtherHousehold);
        result.User.Should().BeNull("the cross-household reject leaks no user data");

        // And no row was created in household A for that email.
        (await _service.GetMembersAsync(HhA)).Should().NotContain(m => m.Email == "bob@b.test");
    }

    // ─── SetWhitelist (toggle) ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetWhitelist_Self_IsForbidden()
    {
        var (result, _) = await _service.SetWhitelistAsync(HhA, currentUserId: 1, targetUserId: 1, isWhitelisted: false);
        result.Should().Be(MemberMutationResult.SelfForbidden);
    }

    [Fact]
    public async Task SetWhitelist_DisableOneOfTwoActive_IsAllowed()
    {
        // HhA has alice + amy active (dan is disabled) ⇒ disabling amy still leaves alice active.
        var (result, user) = await _service.SetWhitelistAsync(HhA, currentUserId: 1, targetUserId: 2, isWhitelisted: false);
        result.Should().Be(MemberMutationResult.Ok);
        user!.IsWhitelisted.Should().BeFalse();
    }

    [Fact]
    public async Task SetWhitelist_DisableLastActive_IsForbidden()
    {
        // Drive HhA down to a single active member (alice), then attempt to disable her as a non-self caller.
        await _service.SetWhitelistAsync(HhA, currentUserId: 1, targetUserId: 2, isWhitelisted: false); // amy off
        var (result, _) = await _service.SetWhitelistAsync(HhA, currentUserId: 2, targetUserId: 1, isWhitelisted: false);
        result.Should().Be(MemberMutationResult.LastActiveForbidden);
    }

    [Fact]
    public async Task SetWhitelist_UnknownMember_IsNotFound()
    {
        var (result, _) = await _service.SetWhitelistAsync(HhA, currentUserId: 1, targetUserId: 999, isWhitelisted: false);
        result.Should().Be(MemberMutationResult.NotFound);
    }

    [Fact]
    public async Task SetWhitelist_CrossHousehold_IsNotFound()
    {
        // Bob (id 9) lives in HhB ⇒ a HhA-scoped toggle must not find him (M1).
        var (result, _) = await _service.SetWhitelistAsync(HhA, currentUserId: 1, targetUserId: 9, isWhitelisted: false);
        result.Should().Be(MemberMutationResult.NotFound);
    }

    // ─── DeleteMember ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMember_Self_IsForbidden()
    {
        var result = await _service.DeleteMemberAsync(HhA, currentUserId: 1, targetUserId: 1);
        result.Should().Be(MemberMutationResult.SelfForbidden);
    }

    [Fact]
    public async Task DeleteMember_NonSelf_WhenMultipleUsers_Deletes()
    {
        var result = await _service.DeleteMemberAsync(HhA, currentUserId: 1, targetUserId: 2);
        result.Should().Be(MemberMutationResult.Ok);
        (await _service.GetMembersAsync(HhA)).Should().NotContain(m => m.Id == 2);
    }

    [Fact]
    public async Task DeleteMember_LastUser_IsForbidden()
    {
        // HhB has a single user (bob). A non-self caller deleting him would empty the household.
        var result = await _service.DeleteMemberAsync(HhB, currentUserId: 999, targetUserId: 9);
        result.Should().Be(MemberMutationResult.LastUserForbidden);
    }

    [Fact]
    public async Task DeleteMember_CrossHousehold_IsNotFound()
    {
        var result = await _service.DeleteMemberAsync(HhA, currentUserId: 1, targetUserId: 9);
        result.Should().Be(MemberMutationResult.NotFound);
    }
}
