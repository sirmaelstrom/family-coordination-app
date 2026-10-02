using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Pages.Household;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class EmailIdentityTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task MixedCaseCookie_ResolvesWhitelistedLowercaseMember_ForMeAndPresence()
    {
        await using var factory = new ChoresWebAppFactory(postgres);
        await factory.EnsureSeededAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, "ALICE@Household-A.Test");

        var response = await client.GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<MeEndpoints.MeDto>();
        me!.UserId.Should().Be(ChoresWebAppFactory.UserAId);
        me.HouseholdId.Should().Be(ChoresWebAppFactory.HouseholdAId);

        var heartbeat = await client.PostAsJsonAsync("/api/presence/heartbeat", new { page = "/dashboard" });
        heartbeat.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MixedCaseRequestClaim_StoresLowercaseEmail_AndFindsPendingRequest()
    {
        var dbFactory = await CreateDatabaseAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "  Request@Home.Test  "), new Claim(ClaimTypes.Name, "Requester")], "Test"));

        var request = new RequestModel(dbFactory, NullLogger<RequestModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } },
            HouseholdName = "Requested Home"
        };
        (await request.OnPostAsync()).Should().BeOfType<RedirectResult>();

        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.HouseholdRequests.SingleAsync()).Email.Should().Be("request@home.test");

        var pending = new PendingModel(dbFactory, NullLogger<PendingModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        await pending.OnGetAsync();
        pending.RequestRecord.Should().NotBeNull();
    }

    // ── Legacy rows (review 5387127961): the old claim-based writers stored mixed case, and no migration rewrote it ──

    [Fact]
    public async Task LegacyMixedCaseUser_IsAuthorizedAndResolved_ForMeAndPresence()
    {
        await using var factory = new ChoresWebAppFactory(postgres);
        await factory.EnsureSeededAsync();
        await AddUsersAsync(factory, new User
        {
            Id = 101, HouseholdId = ChoresWebAppFactory.HouseholdAId, Email = "Carol@Household-A.Test",
            DisplayName = "Carol A", Initials = "CA", IsWhitelisted = true, CreatedAt = DateTime.UtcNow
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, "Carol@Household-A.Test");

        var response = await client.GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a legacy mixed-case row still belongs to its owner");
        var me = await response.Content.ReadFromJsonAsync<MeEndpoints.MeDto>();
        me!.UserId.Should().Be(101);
        me.HouseholdId.Should().Be(ChoresWebAppFactory.HouseholdAId);

        var heartbeat = await client.PostAsJsonAsync("/api/presence/heartbeat", new { page = "/dashboard" });
        heartbeat.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CaseDuplicateUsersAcrossHouseholds_AreRefused_NotResolvedToEither()
    {
        await using var factory = new ChoresWebAppFactory(postgres);
        await factory.EnsureSeededAsync();
        await AddUsersAsync(factory,
            new User
            {
                Id = 102, HouseholdId = ChoresWebAppFactory.HouseholdAId, Email = "Dave@Household-A.Test",
                DisplayName = "Dave A", Initials = "DA", IsWhitelisted = true, CreatedAt = DateTime.UtcNow
            },
            new User
            {
                Id = 103, HouseholdId = ChoresWebAppFactory.HouseholdBId, Email = "dave@household-a.test",
                DisplayName = "Dave B", Initials = "DB", IsWhitelisted = true, CreatedAt = DateTime.UtcNow
            });
        var logs = new CapturingLoggerProvider();
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, "dave@household-a.test");

        var response = await client.GetAsync("/api/me");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "two rows that differ only in case must not resolve to either household");
        logs.Messages.Should().Contain(m => m.Contains("matches more than one user when case is ignored"),
            "the refusal is logged, never silent");
    }

    [Theory]
    [InlineData(HouseholdRequestStatus.Pending)]
    [InlineData(HouseholdRequestStatus.Rejected)]
    public async Task LegacyMixedCaseRequest_IsFoundByItsOwner_AndNotDuplicated(HouseholdRequestStatus status)
    {
        var dbFactory = await CreateDatabaseAsync();
        int legacyId;
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            var legacy = new HouseholdRequest
            {
                Email = "Request@Home.Test", DisplayName = "Requester", HouseholdName = "Old Home", Status = status,
                RequestedAt = DateTime.UtcNow.AddDays(-1)
            };
            seed.HouseholdRequests.Add(legacy);
            await seed.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "Request@Home.Test"), new Claim(ClaimTypes.Name, "Requester")], "Test"));
        var request = new RequestModel(dbFactory, NullLogger<RequestModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } },
            HouseholdName = "New Home"
        };
        (await request.OnPostAsync()).Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/household/pending");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var rows = await db.HouseholdRequests.ToListAsync();
            rows.Should().ContainSingle("the legacy request is found, so no second row is inserted")
                .Which.Id.Should().Be(legacyId);
            rows[0].Status.Should().Be(HouseholdRequestStatus.Pending);
        }

        var pending = new PendingModel(dbFactory, NullLogger<PendingModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        await pending.OnGetAsync();
        pending.RequestRecord.Should().NotBeNull("the pending page shows the legacy request");
        pending.RequestRecord!.Id.Should().Be(legacyId);
    }

    [Fact]
    public async Task CaseDuplicateRequests_ResolveToTheNewest_WithAWarning()
    {
        var dbFactory = await CreateDatabaseAsync();
        int newestId;
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            var older = new HouseholdRequest
            {
                Email = "dup@home.test", DisplayName = "Dup", HouseholdName = "Older", Status = HouseholdRequestStatus.Rejected,
                RequestedAt = DateTime.UtcNow.AddDays(-2)
            };
            var newest = new HouseholdRequest
            {
                Email = "Dup@Home.Test", DisplayName = "Dup", HouseholdName = "Newest", Status = HouseholdRequestStatus.Pending,
                RequestedAt = DateTime.UtcNow.AddDays(-1)
            };
            seed.HouseholdRequests.AddRange(older, newest);
            await seed.SaveChangesAsync();
            newestId = newest.Id;
        }

        var logger = new CapturingLogger<PendingModel>();
        var pending = new PendingModel(dbFactory, logger)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "dup@home.test")], "Test"))
                }
            }
        };
        await pending.OnGetAsync();

        pending.RequestRecord!.Id.Should().Be(newestId);
        logger.Messages.Should().ContainSingle(m => m.Contains("matches more than one household request"));
    }

    [Fact]
    public async Task Approve_RefusesWhenALegacyMixedCaseUserHoldsTheEmail()
    {
        var dbFactory = await CreateDatabaseAsync();
        int requestId;
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            var home = new Household { Name = "Legacy Home", CreatedAt = DateTime.UtcNow };
            seed.Households.Add(home);
            await seed.SaveChangesAsync();
            seed.Users.Add(new User
            {
                HouseholdId = home.Id,
                Email = "Owner@Home.Test",
                DisplayName = "Owner",
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow
            });
            var request = new HouseholdRequest
            {
                Email = "owner@home.test",
                DisplayName = "Owner",
                HouseholdName = "Second Home",
                RequestedAt = DateTime.UtcNow
            };
            seed.HouseholdRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        var service = new HouseholdRequestService(dbFactory, NullLogger<HouseholdRequestService>.Instance);
        var result = await service.ApproveAsync(requestId, "admin@site.test");

        result.Outcome.Should().Be(ReviewOutcome.EmailInUse,
            "a lowercase insert beside the legacy row would create a case-duplicate the unique index allows");
        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.Users.CountAsync()).Should().Be(1);
        (await db.Households.CountAsync()).Should().Be(1, "the refusal precedes the household insert");
    }

    private static async Task AddUsersAsync(ChoresWebAppFactory factory, params User[] users)
    {
        await using var db = await new PostgresDbContextFactory(factory.ConnectionString).CreateDbContextAsync();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ApprovalOfMixedCaseRequest_StoresLowercaseOwnerAndReviewer()
    {
        var dbFactory = await CreateDatabaseAsync();
        int requestId;
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            var request = new HouseholdRequest
            {
                Email = "  Owner@Home.Test  ", DisplayName = "Owner", HouseholdName = "Approved Home",
                RequestedAt = DateTime.UtcNow
            };
            seed.HouseholdRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        var service = new HouseholdRequestService(dbFactory, NullLogger<HouseholdRequestService>.Instance);
        var result = await service.ApproveAsync(requestId, "  Admin@Site.Test  ");
        result.Outcome.Should().Be(ReviewOutcome.Ok);

        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.Users.SingleAsync()).Email.Should().Be("owner@home.test");
        (await db.HouseholdRequests.SingleAsync()).ReviewedBy.Should().Be("admin@site.test");
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> CreateDatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        var factory = new PostgresDbContextFactory(connectionString);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return factory;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger<object>(Messages);

        public void Dispose() { }
    }

    private sealed class CapturingLogger<T>(ConcurrentQueue<string>? sink = null) : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = sink ?? new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
