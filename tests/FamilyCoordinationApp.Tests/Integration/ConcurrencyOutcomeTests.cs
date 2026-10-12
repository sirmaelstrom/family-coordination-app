using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Calendar;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest f1dc7a24: the outcome of a lost race on real Postgres xmin, forced with <see cref="InterleavingDbContextFactory"/>
/// (a rival write commits between the service's load and its save). fca#132 turned these races from 500s into 409s;
/// this pins the outcomes the user should see instead:
/// <list type="bullet">
/// <item>a concurrent invite accept answers the documented "already used" envelope, not a 409;</item>
/// <item>a double-clicked invite cancel, or calendar-token revoke, succeeds because the persisted state is already what
/// it asked for;</item>
/// <item>a calendar rotation landing under a revoke is still a 409 (a real conflict: a new active token exists).</item>
/// </list>
/// Negative control: on the base tree each of the first three throws <see cref="ConcurrencyConflictException"/>.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ConcurrencyOutcomeTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int Inviting = 1;
    private const int Accepting = 2;
    private const int Third = 3;
    private const string Code = "ABC234";
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private string _connectionString = default!;
    private PostgresDbContextFactory _plain = default!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        _plain = new PostgresDbContextFactory(_connectionString);
        await using var ctx = _plain.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Households.AddRange(
            new Household { Id = Inviting, Name = "Inviting", CreatedAt = Now },
            new Household { Id = Accepting, Name = "Accepting", CreatedAt = Now },
            new Household { Id = Third, Name = "Third", CreatedAt = Now });
        ctx.Users.AddRange(
            new User { Id = 1, HouseholdId = Inviting, Email = "i@x.test", DisplayName = "I", Initials = "I", IsWhitelisted = true, CreatedAt = Now },
            new User { Id = 2, HouseholdId = Accepting, Email = "a@x.test", DisplayName = "A", Initials = "A", IsWhitelisted = true, CreatedAt = Now },
            new User { Id = 3, HouseholdId = Third, Email = "t@x.test", DisplayName = "T", Initials = "T", IsWhitelisted = true, CreatedAt = Now });
        ctx.HouseholdInvites.Add(new HouseholdInvite
        {
            HouseholdId = Inviting, InviteCode = Code, CreatedByUserId = 1, CreatedAt = Now, ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HouseholdConnectionService Connections(Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, NullLogger<HouseholdConnectionService>.Instance, Services.TestClocks.System);

    private HouseholdCalendarTokenService Tokens(Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, Services.TestClocks.System);

    /// <summary>The token service as the inviting household's caller: its inserts need the tenant to stamp HouseholdId.</summary>
    private HouseholdCalendarTokenService CallerTokens() => Tokens(_plain.WithTenant(TestTenancy.Caller(Inviting)));

    [Fact]
    public async Task A_concurrent_accept_answers_the_already_used_envelope()
    {
        // Household 3 accepts the same code between household 2's read and its save.
        var interleaved = new InterleavingDbContextFactory(_connectionString,
            async () => (await Connections(_plain).AcceptInviteAsync(Code, Third, 3)).Success.Should().BeTrue());

        var result = await Connections(interleaved).AcceptInviteAsync(Code, Accepting, 2);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("This invite code has already been used.");
        await using var check = _plain.CreateDbContext();
        (await check.HouseholdConnections.CountAsync()).Should().Be(1, "only the winner connected");
    }

    [Fact]
    public async Task A_double_clicked_cancel_succeeds_and_leaves_no_active_invite()
    {
        var interleaved = new InterleavingDbContextFactory(_connectionString,
            () => Connections(_plain).InvalidateInviteAsync(Inviting));

        await Connections(interleaved).InvalidateInviteAsync(Inviting);

        await using var check = _plain.CreateDbContext();
        (await check.HouseholdInvites.CountAsync(i => !i.IsUsed)).Should().Be(0);
    }

    [Fact]
    public async Task A_double_clicked_calendar_revoke_succeeds()
    {
        await CallerTokens().CreateOrRotateAsync(Inviting);
        var interleaved = new InterleavingDbContextFactory(_connectionString, () => CallerTokens().RevokeAsync(Inviting));

        await Tokens(interleaved).RevokeAsync(Inviting);

        await using var check = _plain.CreateDbContext();
        (await check.HouseholdCalendarTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(0);
    }

    [Fact]
    public async Task A_rotation_landing_under_a_revoke_is_still_a_conflict()
    {
        await CallerTokens().CreateOrRotateAsync(Inviting);
        var interleaved = new InterleavingDbContextFactory(_connectionString,
            async () => await CallerTokens().CreateOrRotateAsync(Inviting));

        var act = () => Tokens(interleaved).RevokeAsync(Inviting);

        await act.Should().ThrowAsync<ConcurrencyConflictException>("the revoke never saw the new active token");
        await using var check = _plain.CreateDbContext();
        (await check.HouseholdCalendarTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(1, "the rotation's token stays active");
    }
}
