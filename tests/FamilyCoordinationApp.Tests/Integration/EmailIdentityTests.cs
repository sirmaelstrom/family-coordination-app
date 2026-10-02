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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
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

        var pending = new PendingModel(dbFactory)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        await pending.OnGetAsync();
        pending.RequestRecord.Should().NotBeNull();
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
}
