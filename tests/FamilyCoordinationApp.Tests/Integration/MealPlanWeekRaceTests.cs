using System.Data.Common;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest f5ca4b08: two first adds into the same new week must leave ONE plan for that week. The interleaving is forced
/// against real Postgres: an interceptor holds the first add at its <c>max("MealPlanId")</c> read, after its week read
/// found nothing, and commits a rival add for the same week there. With the unique index on
/// <c>(HouseholdId, WeekStartDate)</c> the first add's insert is a unique violation, <c>IKeyAllocator.InsertAsync</c> retries,
/// and the retry finds the rival's plan.
/// <para>The schema comes from the real migrations (<c>MigrateAsync</c>), so the test fails if the migration lacks the
/// index even when the model has it. Negative control: on the base tree (no index) it is red with 2 plans for the week
/// (receipted in PR #138).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MealPlanWeekRaceTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdId = 1;
    private static readonly DateOnly Week = new(2026, 10, 12); // a Monday
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private string _connectionString = default!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var ctx = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        await ctx.Database.MigrateAsync();
        ctx.Households.Add(new Household { Id = HouseholdId, Name = "Race Week House", CreatedAt = Now });
        ctx.Users.Add(new User { Id = 1, HouseholdId = HouseholdId, Email = "a@race.test", DisplayName = "A", Initials = "A", IsWhitelisted = true, CreatedAt = Now });
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Two_first_adds_into_one_week_leave_one_plan_with_both_entries()
    {
        var rival = Service(new PostgresDbContextFactory(_connectionString).WithTenant(TestTenancy.Caller(HouseholdId)));
        var interceptor = new RivalAtMaxRead(() =>
            rival.AddMealAsync(HouseholdId, Week, MealType.Dinner, null, "Rival tacos", userId: 1));
        var first = Service(new InterceptedFactory(_connectionString, interceptor, TestTenancy.Caller(HouseholdId)));

        await first.AddMealAsync(HouseholdId, Week, MealType.Lunch, null, "First soup", userId: 1);

        interceptor.Fired.Should().BeTrue("the rival must have committed between the first add's week read and its max read");
        await using var verify = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        var plans = await verify.MealPlans.Where(mp => mp.HouseholdId == HouseholdId && mp.WeekStartDate == Week).ToListAsync();
        plans.Should().ContainSingle("one household has one plan per week");
        (await verify.MealPlanEntries.Where(e => e.MealPlanId == plans[0].MealPlanId).Select(e => e.CustomMealName).ToListAsync())
            .Should().BeEquivalentTo(["Rival tacos", "First soup"]);
    }

    private static MealPlanService Service(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, NullLogger<MealPlanService>.Instance, Services.TestClocks.System, Services.TestKeys.Allocator);

    /// <summary>Runs <paramref name="rival"/> to completion once, just before the first <c>max("MealPlanId")</c> read.</summary>
    private sealed class RivalAtMaxRead(Func<Task> rival) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await MaybeFireAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeFireAsync(command);
            return result;
        }

        private async Task MaybeFireAsync(DbCommand command)
        {
            if (Fired || !command.CommandText.Contains("max(", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("\"MealPlanId\"", StringComparison.Ordinal)
                || command.CommandText.Contains("\"MealPlanEntries\"", StringComparison.Ordinal))
            {
                return;
            }
            Fired = true;
            await rival();
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor interceptor, FamilyCoordinationApp.Tenancy.ITenantContext tenant)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            TestTenancy.Context(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(interceptor)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options,
                tenant);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
