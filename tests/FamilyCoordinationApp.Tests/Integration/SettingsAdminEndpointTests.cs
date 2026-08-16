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
/// <para>The <c>Feedback_Submit_*</c> block covers <c>POST /api/settings/feedback</c>. Its load-bearing assertions
/// are server-derived attribution (a body-supplied householdId/userId is ignored) and a non-empty body on every 4xx
/// (an empty one is re-executed through the GET-only <c>/not-found</c> page and reaches the caller as some other
/// status).</para>
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
    public async Task Approve_ForcedMidTransactionFailure_RollsBackFully_NoOrphanHousehold_Returns409(/* R-C2 + council R1 */)
    {
        // Seed a pending request whose email is ALREADY a user (alice). Approve creates the household (1st
        // SaveChanges) then the user — which violates the unique Users.Email index on the 2nd SaveChanges, inside
        // the same transaction. The whole unit of work must roll back (R-C2 atomicity: no orphan household, request
        // still pending) AND surface as a clean 409, not an opaque 500 (council R1).
        var requestId = await SeedRequestAsync("Rollback Household", ChoresWebAppFactory.UserAEmail, "Dup Owner");

        var resp = await AdminClient.PostAsync($"{RequestsUrl}/{requestId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "a duplicate email is a clean 409, not a 500");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("already exists");

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Households.AnyAsync(h => h.Name == "Rollback Household"))
            .Should().BeFalse("the household INSERT must roll back with the failed user INSERT (R-C2 atomicity)");
        (await ctx.HouseholdRequests.FindAsync(requestId))!.Status
            .Should().Be(HouseholdRequestStatus.Pending, "a fully-rolled-back approve leaves the request pending");
    }

    [Fact]
    public async Task Reject_WithOversizedReason_Returns400(/* council R4: guard the 500-char column limit */)
    {
        var requestId = await SeedRequestAsync("Too Long Co", "tl@example.com", "TL Owner");

        var resp = await AdminClient.PostAsJsonAsync($"{RequestsUrl}/{requestId}/reject",
            new { reason = new string('x', 501) }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.HouseholdRequests.FindAsync(requestId))!.Status
            .Should().Be(HouseholdRequestStatus.Pending, "a rejected (400) reject must not change the request");
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

    // ─── Feedback SUBMIT (POST /) ─────────────────────────────────────────────────

    /// <summary>
    /// The end-to-end assertion: a regular user's submission is stored, attributed to THEM, and comes back on
    /// their own dual-mode GET.
    /// </summary>
    [Fact]
    public async Task Feedback_Submit_StoresItem_AttributedToCaller_AndItAppearsInTheirOwnList()
    {
        var resp = await NonAdminClient.PostAsJsonAsync(
            $"{FeedbackUrl}/",
            new { type = "bug", message = "  The shopping list drops items.  ", currentPage = "/shopping-list" },
            Json);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var ctx = await DbFactory.CreateDbContextAsync())
        {
            var stored = await ctx.Feedbacks.SingleAsync();
            stored.Message.Should().Be("The shopping list drops items.", "the message is stored trimmed");
            stored.Type.Should().Be(FeedbackType.Bug);
            stored.CurrentPage.Should().Be("/shopping-list");
            stored.UserId.Should().Be(ChoresWebAppFactory.UserBId, "attribution comes from the caller's cookie");
            stored.HouseholdId.Should().Be(ChoresWebAppFactory.HouseholdBId);
            stored.IsRead.Should().BeFalse();
            stored.IsResolved.Should().BeFalse();
        }

        var list = (await NonAdminClient.GetFromJsonAsync<FeedbackListWire>($"{FeedbackUrl}/", Json))!;
        list.items.Should().ContainSingle().Which.message.Should().Be("The shopping list drops items.");
    }

    /// <summary>
    /// M1: the submit body carries no ids, and a caller cannot smuggle one in — extra fields are ignored and the row
    /// still lands in the CALLER's household, invisible to the other one.
    /// </summary>
    [Fact]
    public async Task Feedback_Submit_IgnoresClientSuppliedScope_AndLandsInTheCallersHousehold()
    {
        // Alice (site admin, household A) submits while claiming to be bob in household B.
        var resp = await AdminClient.PostAsJsonAsync(
            $"{FeedbackUrl}/",
            new
            {
                type = "general",
                message = "Attribution probe.",
                householdId = ChoresWebAppFactory.HouseholdBId,
                userId = ChoresWebAppFactory.UserBId,
                isRead = true,
                isResolved = true,
            },
            Json);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var ctx = await DbFactory.CreateDbContextAsync())
        {
            var stored = await ctx.Feedbacks.SingleAsync();
            stored.HouseholdId.Should().Be(ChoresWebAppFactory.HouseholdAId, "the household comes from the cookie, not the body");
            stored.UserId.Should().Be(ChoresWebAppFactory.UserAId);
            stored.IsRead.Should().BeFalse("lifecycle flags are admin-only mutations, not submit fields");
            stored.IsResolved.Should().BeFalse();
        }

        // And it is invisible to the other household (the M1 read scope still holds for submitted items).
        var bobList = (await NonAdminClient.GetFromJsonAsync<FeedbackListWire>($"{FeedbackUrl}/", Json))!;
        bobList.items.Should().BeEmpty();
    }

    /// <summary>The three camelCase wire values map to the enum (R-C10), case-insensitively.</summary>
    [Theory]
    [InlineData("bug", FeedbackType.Bug)]
    [InlineData("featureRequest", FeedbackType.FeatureRequest)]
    [InlineData("FeatureRequest", FeedbackType.FeatureRequest)]
    [InlineData("general", FeedbackType.General)]
    public async Task Feedback_Submit_ParsesWireType(string wire, FeedbackType expected)
    {
        (await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { type = wire, message = "x" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.SingleAsync()).Type.Should().Be(expected);
    }

    /// <summary>
    /// One case per way a type can be invalid. The numeric and comma forms are the reason the parse is a
    /// whitelist and not <c>Enum.TryParse</c>+<c>IsDefined</c>, which accepted both (<c>"bug,general"</c> ⇒
    /// <c>0|2</c> ⇒ <c>General</c>, stored under a type the caller never named).
    /// </summary>
    [Theory]
    [InlineData("0", "numeric")]
    [InlineData("bug,general", "comma/flags")]
    [InlineData("complaint", "plain unknown")]
    public async Task Feedback_Submit_RejectsInvalidType(string wire, string form)
    {
        var resp = await NonAdminClient.PostAsJsonAsync(
            $"{FeedbackUrl}/", new { type = wire, message = "invalid type" }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} is not on the wire contract", form);
        (await ReadMessageAsync(resp)).Should().NotBeNullOrWhiteSpace();

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Truncating mid-surrogate-pair leaves invalid UTF-16 that Npgsql's encoder rejects, turning a
    /// caller-controlled <c>currentPage</c> into a 500. Without the boundary step-back this test 500s.
    /// </summary>
    [Fact]
    public async Task Feedback_Submit_LongPageEndingMidSurrogatePair_DoesNotBlowUpTheWrite()
    {
        // 499 ASCII + U+1F600 (2 UTF-16 units, so the high half sits at index 499) + padding past the cut.
        var page = new string('a', 499) + "\U0001F600" + new string('b', 200);
        char.IsHighSurrogate(page[499]).Should().BeTrue("the probe must actually straddle the cut to be meaningful");

        var resp = await NonAdminClient.PostAsJsonAsync(
            $"{FeedbackUrl}/", new { type = "general", message = "surrogate boundary", currentPage = page }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        var stored = (await ctx.Feedbacks.SingleAsync()).CurrentPage!;
        stored.Should().HaveLength(499, "the orphaned high surrogate is dropped rather than cut in half");
        char.IsHighSurrogate(stored[^1]).Should().BeFalse("a stored value must be valid UTF-16");
        stored.Should().Be(new string('a', 499));
    }

    /// <summary>An omitted type defaults to General (the dialog's default) rather than 400ing.</summary>
    [Fact]
    public async Task Feedback_Submit_OmittedType_DefaultsToGeneral()
    {
        (await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { message = "no type field" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.SingleAsync()).Type.Should().Be(FeedbackType.General);
    }

    /// <summary>
    /// The body is as much the assertion as the status: an empty non-GET 4xx re-executes through the GET-only
    /// /not-found page and reaches the caller as something else entirely. Both cases matter — dropping the
    /// endpoint's <c>.Trim()</c> would break only the whitespace-only one.
    /// </summary>
    [Theory]
    [InlineData("", "blank message")]
    [InlineData("     ", "whitespace-only message")]
    public async Task Feedback_Submit_BlankMessage_Returns400_WithBody(string message, string because)
    {
        var resp = await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { type = "bug", message }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await ReadMessageAsync(resp)).Should().NotBeNullOrWhiteSpace("an empty 4xx body would surface as a 405");

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The Message column is varchar(4000): an oversized direct-API message must be a clean 400, not a
    /// varchar-overflow 500 (parity with RejectRequest's 500-char guard). 4000 exactly is accepted.
    /// </summary>
    [Fact]
    public async Task Feedback_Submit_OversizedMessage_Returns400_WithBody_But4000ExactlyIsAccepted()
    {
        var tooLong = new string('x', 4001);
        var resp = await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { type = "bug", message = tooLong }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadMessageAsync(resp)).Should().NotBeNullOrWhiteSpace();

        var atLimit = new string('y', 4000);
        (await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { type = "bug", message = atLimit }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();
        (await ctx.Feedbacks.SingleAsync()).Message.Should().HaveLength(4000);
    }

    /// <summary>
    /// UserAgent is read from the request headers (a client-supplied one is worthless as a diagnostic) and, like
    /// CurrentPage, is TRUNCATED to its 500-char column rather than rejected — a long User-Agent or path must never
    /// cost the user their bug report.
    /// </summary>
    [Fact]
    public async Task Feedback_Submit_CapturesUserAgentFromHeaders_AndTruncatesDiagnosticsTo500()
    {
        var longPath = "/recipes/" + new string('p', 600);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{FeedbackUrl}/")
        {
            Content = JsonContent.Create(new { type = "bug", message = "diagnostics", currentPage = longPath }, options: Json),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", new string('u', 700));

        (await NonAdminClient.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Same test, second case: an absent diagnostic must store null, not "".
        (await NonAdminClient.PostAsJsonAsync($"{FeedbackUrl}/", new { type = "general", message = "no page" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var ctx = await DbFactory.CreateDbContextAsync();

        var truncated = await ctx.Feedbacks.SingleAsync(f => f.Message == "diagnostics");
        truncated.UserAgent.Should().Be(new string('u', 500), "the 500-char column is a truncation boundary, not a rejection");
        truncated.CurrentPage.Should().Be(longPath[..500]);

        var noPage = await ctx.Feedbacks.SingleAsync(f => f.Message == "no page");
        noPage.CurrentPage.Should().BeNull("an empty diagnostic is null, else the DTO renders an empty page link");
    }

    /// <summary>Reads the `{ message }` field every 4xx on this surface is required to carry.</summary>
    private static async Task<string?> ReadMessageAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
    }
}
