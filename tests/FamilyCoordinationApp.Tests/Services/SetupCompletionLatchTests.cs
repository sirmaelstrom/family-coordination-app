using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// <see cref="SetupService.IsSetupCompleteAsync"/> is called twice per request (the first-run middleware and
/// <c>WhitelistedEmailHandler</c>), so it must be a cheap read that latches. These run on the InMemory provider,
/// which has no relational migrator at all — the suite could not exist while the method still called
/// <c>Database.MigrateAsync</c>.
/// </summary>
public sealed class SetupCompletionLatchTests
{
    private static IDbContextFactory<ApplicationDbContext> InMemoryDbFactory(bool withHousehold)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        if (withHousehold)
        {
            using var seed = new ApplicationDbContext(options);
            seed.Households.Add(new Household { Id = 1, Name = "H", CreatedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }

        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));
        return mock.Object;
    }

    /// <summary>A db factory that throws if touched — proves a code path never queries the database.</summary>
    private static IDbContextFactory<ApplicationDbContext> ThrowingDbFactory()
    {
        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database must not be touched on this path"));
        return mock.Object;
    }

    private static SetupService Service(IDbContextFactory<ApplicationDbContext> dbFactory, SetupCompletionLatch latch) =>
        new(dbFactory, latch, NullLogger<SetupService>.Instance);

    [Fact]
    public async Task NoHousehold_ReturnsFalse_AndKeepsQuerying()
    {
        var latch = new SetupCompletionLatch();
        var service = Service(InMemoryDbFactory(withHousehold: false), latch);

        (await service.IsSetupCompleteAsync()).Should().BeFalse();
        latch.IsComplete.Should().BeFalse("an incomplete setup must stay re-checkable — the latch is one-way TRUE");
    }

    [Fact]
    public async Task OnceAHouseholdIsObserved_TheAnswerIsLatched_AndTheDatabaseIsNoLongerTouched()
    {
        var latch = new SetupCompletionLatch();

        (await Service(InMemoryDbFactory(withHousehold: true), latch).IsSetupCompleteAsync())
            .Should().BeTrue();

        // Same (singleton) latch, a factory that throws on any use. IsSetupCompleteAsync swallows exceptions and
        // returns false, so reaching the database at all would surface here as false rather than as a throw.
        (await Service(ThrowingDbFactory(), latch).IsSetupCompleteAsync())
            .Should().BeTrue("the latched answer must be served without a query");
    }

    [Fact]
    public async Task AFreshLatch_QueriesAgain()
    {
        var dbFactory = InMemoryDbFactory(withHousehold: true);

        (await Service(dbFactory, new SetupCompletionLatch()).IsSetupCompleteAsync()).Should().BeTrue();
        (await Service(ThrowingDbFactory(), new SetupCompletionLatch()).IsSetupCompleteAsync())
            .Should().BeFalse("a new process starts with an empty latch and must re-derive the answer");
    }
}
