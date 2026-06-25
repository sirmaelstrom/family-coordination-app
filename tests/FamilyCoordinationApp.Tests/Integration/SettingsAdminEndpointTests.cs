using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the Settings island C (Admin) endpoints (<c>/api/settings/household-requests</c> +
/// <c>/api/settings/feedback</c>) through the real HTTP pipeline against real Postgres. Reuses
/// <see cref="ChoresWebAppFactory"/>'s two-household seed (A id=1 alice+amy, B id=2 bob) and makes
/// <c>alice@household-a.test</c> the SITE ADMIN (via <c>SITE_ADMIN_EMAILS</c>) so the role split is exercised.
/// Each test method gets its OWN freshly-seeded database. Proves the three load-bearing findings each with its own
/// test: R-C1 (feedback IDOR is blocked), R-C2 (approve is atomic — a forced mid-approve failure rolls back fully),
/// R-C3 (an already-reviewed request is a 409, never a second household); plus the 403 site-admin gate and the
/// dual-mode feedback visibility.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class SettingsAdminEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // alice = site admin (household A); bob = regular user (household B).
    private HttpClient AdminClient => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient NonAdminClient => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private const string RequestsUrl = "/api/settings/household-requests";
    private const string FeedbackUrl = "/api/settings/feedback";

    private IDbContextFactory<ApplicationDbContext> DbFactory =>
        _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

    // Wire shapes (camelCase via JsonSerializerDefaults.Web; enums as camelCase strings).
    private sealed record RequestWire(int id, string householdName, string displayName, string email, string status,
        string requestedAt, string? reviewedAt, string? reviewedBy, string? rejectionReason);
    private sealed record SummaryWire(int householdId, string name, int memberCount, string createdAt);
    private sealed record RequestsWire(List<RequestWire> requests, List<SummaryWire> households);
    private sealed record FeedbackWire(int id, string type, string message, string? currentPage, bool isRead,
        bool isResolved, string createdAt, string? authorName, bool authorDeleted);
    private sealed record FeedbackListWire(bool isSiteAdmin, List<FeedbackWire> items);

    // ─── Seed helpers ─────────────────────────────────────────────────────────────

    private async Task<int> SeedRequestAsync(
        string householdName, string email, string displayName,
        HouseholdRequestStatus status = HouseholdRequestStatus.Pending)
    {
        await using var ctx = await DbFactory.CreateDbContextAsync();
        var req = new HouseholdRequest
        {
            Email = email,
            DisplayName = displayName,
            HouseholdName = householdName,
            GoogleId = null,
            Status = status,
            RequestedAt = DateTime.UtcNow,
        };
        ctx.HouseholdRequests.Add(req);
        await ctx.SaveChangesAsync();
        return req.Id;
    }

    private async Task<int> SeedFeedbackAsync(
        int householdId, int? userId = null, FeedbackType type = FeedbackType.Bug,
        bool isRead = false, bool isResolved = false)
    {
        await using var ctx = await DbFactory.CreateDbContextAsync();
        var fb = new Feedback
        {
            HouseholdId = householdId,
            UserId = userId,
            Type = type,
            Message = "test feedback",
            CreatedAt = DateTime.UtcNow,
            IsRead = isRead,
            IsResolved = isResolved,
        };
        ctx.Feedbacks.Add(fb);
        await ctx.SaveChangesAsync();
        return fb.Id;
    }

    // ─── Site-admin 403 gate (R-C8 — the C test for these routes is the gate, not M1) ─────────────

    [Fact]
    public async Task HouseholdRequests_NonSiteAdmin_Gets403_OnEveryRoute()
    {
        var requestId = await SeedRequestAsync("The Greens", "pat@example.com", "Pat Green");

        (await NonAdminClient.GetAsync($"{RequestsUrl}/"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NonAdminClient.PostAsync($"{RequestsUrl}/{requestId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var reject = await NonAdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject", new { reason = "no" }, Json);
        reject.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 403 carries a non-empty body (so the global re-execute doesn't turn it into a 405 on the POSTs).
        (await reject.Content.ReadAsStringAsync()).Should().Contain("Site admin");

        // The request was untouched by the rejected calls.
        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.HouseholdRequests.FindAsync(requestId))!.Status.Should().Be(HouseholdRequestStatus.Pending);
    }

    [Fact]
    public async Task HouseholdRequests_Unauthenticated_Returns401()
    {
        (await _factory.CreateAnonymousClient().GetAsync($"{RequestsUrl}/"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HouseholdRequests_SiteAdmin_SeesRequestsPendingFirst_AndHouseholdsWithMemberCounts()
    {
        await SeedRequestAsync("Approved Co", "sam@example.com", "Sam", HouseholdRequestStatus.Approved);
        await SeedRequestAsync("Pending Co", "pat@example.com", "Pat", HouseholdRequestStatus.Pending);

        var dto = (await AdminClient.GetFromJsonAsync<RequestsWire>($"{RequestsUrl}/", Json))!;

        dto.requests.Should().HaveCount(2);
        dto.requests[0].status.Should().Be("pending", "pending requests sort first (parity)");

        // The two seeded households (A id=1: alice+amy = 2 members; B id=2: bob = 1), member counts populated (R-C8).
        dto.households.Should().Contain(h => h.householdId == ChoresWebAppFactory.HouseholdAId && h.memberCount == 2);
        dto.households.Should().Contain(h => h.householdId == ChoresWebAppFactory.HouseholdBId && h.memberCount == 1);
    }

    // ─── Approve: the atomic transaction (R-C2) ───────────────────────────────────

    [Fact]
    public async Task Approve_CreatesHousehold_WhitelistedUser_AndSeedsNineCategories_MarksApproved()
    {
        var requestId = await SeedRequestAsync("The Approved", "newowner@example.com", "New Owner");

        var resp = await AdminClient.PostAsync($"{RequestsUrl}/{requestId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var summary = (await resp.Content.ReadFromJsonAsync<SummaryWire>(Json))!;
        summary.name.Should().Be("The Approved");
        summary.memberCount.Should().Be(1);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        var household = await ctx.Households.Include(h => h.Users)
            .FirstOrDefaultAsync(h => h.Name == "The Approved");
        household.Should().NotBeNull();

        household!.Users.Should().ContainSingle()
            .Which.Should().Match<User>(u => u.Email == "newowner@example.com" && u.IsWhitelisted);

        // Default categories were seeded inside the same transaction (R-C2).
        var categoryCount = await ctx.Categories.IgnoreQueryFilters()
            .CountAsync(c => c.HouseholdId == household.Id);
        categoryCount.Should().Be(9);

        var request = await ctx.HouseholdRequests.FindAsync(requestId);
        request!.Status.Should().Be(HouseholdRequestStatus.Approved);
        request.ReviewedBy.Should().Be(ChoresWebAppFactory.UserAEmail);
        request.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Approve_AlreadyReviewed_Returns409_AndDoesNotCreateASecondHousehold()
    {
        var requestId = await SeedRequestAsync("Once Only", "once@example.com", "Once Owner");

        var first = await AdminClient.PostAsync($"{RequestsUrl}/{requestId}/approve", null);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // R-C3: a second approve (e.g. a stale 30s-poll view or a second admin) must NOT spin up a second household.
        var second = await AdminClient.PostAsync($"{RequestsUrl}/{requestId}/approve", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("already been reviewed");

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Households.CountAsync(h => h.Name == "Once Only")).Should().Be(1);
    }

    [Fact]
    public async Task Approve_ForcedMidTransactionFailure_RollsBackFully_NoOrphanHousehold(/* R-C2 */)
    {
        // Seed a pending request whose email is ALREADY a user (alice). Approve creates the household (1st
        // SaveChanges) then the user — which violates the unique Users.Email index on the 2nd SaveChanges, inside
        // the same transaction. The whole unit of work must roll back: no orphan household, request still pending.
        var requestId = await SeedRequestAsync("Rollback Household", ChoresWebAppFactory.UserAEmail, "Dup Owner");

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IHouseholdRequestService>();

        var act = async () => await service.ApproveAsync(requestId, ChoresWebAppFactory.UserAEmail);
        await act.Should().ThrowAsync<DbUpdateException>("the duplicate email violates the unique index mid-transaction");

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Households.AnyAsync(h => h.Name == "Rollback Household"))
            .Should().BeFalse("the household INSERT must roll back with the failed user INSERT (R-C2 atomicity)");
        (await ctx.HouseholdRequests.FindAsync(requestId))!.Status
            .Should().Be(HouseholdRequestStatus.Pending, "a fully-rolled-back approve leaves the request pending");
    }

    [Fact]
    public async Task Approve_UnknownRequest_Returns404()
    {
        (await AdminClient.PostAsync($"{RequestsUrl}/999999/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Reject (optional reason R-C7; already-reviewed 409 R-C3) ──────────────────

    [Fact]
    public async Task Reject_WithReason_MarksRejected_AndStoresReason()
    {
        var requestId = await SeedRequestAsync("Reject Co", "rej@example.com", "Rej Owner");

        var resp = await AdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject",
            new { reason = "Duplicate household." }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        var request = await ctx.HouseholdRequests.FindAsync(requestId);
        request!.Status.Should().Be(HouseholdRequestStatus.Rejected);
        request.RejectionReason.Should().Be("Duplicate household.");
        request.ReviewedBy.Should().Be(ChoresWebAppFactory.UserAEmail);
    }

    [Fact]
    public async Task Reject_WithEmptyReason_IsAccepted_NoBadRequest(/* R-C7: reason is optional */)
    {
        var requestId = await SeedRequestAsync("No Reason Co", "nr@example.com", "NR Owner");

        var resp = await AdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject", new { reason = "" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.HouseholdRequests.FindAsync(requestId))!.Status.Should().Be(HouseholdRequestStatus.Rejected);
    }

    [Fact]
    public async Task Reject_AlreadyReviewed_Returns409()
    {
        var requestId = await SeedRequestAsync("Twice Co", "twice@example.com", "Twice Owner");

        (await AdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject", new { reason = "first" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject", new { reason = "second" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ─── Feedback dual-mode visibility ────────────────────────────────────────────

    [Fact]
    public async Task Feedback_SiteAdmin_SeesAllHouseholds_NonAdmin_SeesOwnOnly()
    {
        await SeedFeedbackAsync(ChoresWebAppFactory.HouseholdAId);
        await SeedFeedbackAsync(ChoresWebAppFactory.HouseholdBId);

        var adminList = (await AdminClient.GetFromJsonAsync<FeedbackListWire>($"{FeedbackUrl}/", Json))!;
        adminList.isSiteAdmin.Should().BeTrue();
        adminList.items.Should().HaveCount(2, "a site admin sees feedback from every household");

        var bobList = (await NonAdminClient.GetFromJsonAsync<FeedbackListWire>($"{FeedbackUrl}/", Json))!;
        bobList.isSiteAdmin.Should().BeFalse();
        bobList.items.Should().ContainSingle("a regular user sees only their own household's feedback (M1)");
    }

    // ─── Feedback mutation IDOR (R-C1 — the security must-fix) ─────────────────────

    [Fact]
    public async Task Feedback_NonAdmin_CannotMutateAnotherHouseholdsItem_Returns404_NoMutation()
    {
        // A feedback row in household A. Bob is in household B → it must be invisible + immutable to him, and the
        // 404 must not leak that it exists (R-C1).
        var foreignId = await SeedFeedbackAsync(ChoresWebAppFactory.HouseholdAId);

        foreach (var verb in new[] { "read", "resolve", "reopen" })
        {
            var resp = await NonAdminClient.PostAsync($"{FeedbackUrl}/{foreignId}/{verb}", null);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, $"non-admin {verb} on another household's feedback is an IDOR → 404");
        }

        // Nothing was mutated.
        await using var ctx = await DbFactory.CreateDbContextAsync();
        var fb = await ctx.Feedbacks.FindAsync(foreignId);
        fb!.IsRead.Should().BeFalse();
        fb.IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Feedback_NonAdmin_CanMutateOwnHouseholdsItem(/* positive control: the 404 above is authz, not a blanket block */)
    {
        var ownId = await SeedFeedbackAsync(ChoresWebAppFactory.HouseholdBId);

        (await NonAdminClient.PostAsync($"{FeedbackUrl}/{ownId}/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.FindAsync(ownId))!.IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task Feedback_SiteAdmin_CanResolveAndReopen_AnyHouseholdsItem()
    {
        var id = await SeedFeedbackAsync(ChoresWebAppFactory.HouseholdBId);

        (await AdminClient.PostAsync($"{FeedbackUrl}/{id}/resolve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var ctx = await DbFactory.CreateDbContextAsync())
        {
            var fb = await ctx.Feedbacks.FindAsync(id);
            fb!.IsResolved.Should().BeTrue();
            fb.IsRead.Should().BeTrue("resolve also marks read (parity)");
        }

        (await AdminClient.PostAsync($"{FeedbackUrl}/{id}/reopen", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var ctx = await DbFactory.CreateDbContextAsync())
        {
            (await ctx.Feedbacks.FindAsync(id))!.IsResolved.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Feedback_Mutation_UnknownId_Returns404()
    {
        (await AdminClient.PostAsync($"{FeedbackUrl}/999999/read", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
