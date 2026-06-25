using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the Settings island C (Admin) aggregate DTOs (M9 — mirrors
/// <see cref="SettingsConnectionsDtoContractTests"/>). Serializes representative <see cref="HouseholdRequestsDto"/>
/// and <see cref="FeedbackListDto"/> values with the SAME options the app registers globally (camelCase Web
/// defaults + <see cref="JsonStringEnumConverter"/> camelCase) and asserts equality with the checked-in
/// <c>Fixtures/Settings/{household-requests,feedback}.json</c>. The island's <c>types.ts</c> mirrors these
/// fixtures; any DTO shape/casing change breaks these tests → forcing the island contract to update in lockstep.
///
/// <para>⚠ ENUMS (R-C10): <see cref="HouseholdRequestStatus"/> + <see cref="FeedbackType"/> serialize as camelCase
/// strings ("pending"/"approved"/"rejected", "bug"/"featureRequest"/"general").</para>
/// <para>⚠ DATES (X5): RequestedAt/ReviewedAt/CreatedAt are full ISO-8601 instants ("…Z"), carried as strings.</para>
/// <para>⚠ AUTHOR (R-C6): the three author cases — live (name + !deleted), deleted (null + deleted), anonymous
/// (null + !deleted).</para>
/// </summary>
public class SettingsAdminDtoContractTests
{
    /// <summary>camelCase Web defaults + the enum→camelCase converter — equivalent to the app's global Minimal-API JSON config.</summary>
    public static readonly JsonSerializerOptions AdminJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", file);

    public static HouseholdRequestsDto BuildRepresentativeRequests() => new(
        Requests:
        [
            new HouseholdRequestDto(3, "The Greens", "Pat Green", "pat@example.com",
                HouseholdRequestStatus.Pending, "2026-06-24T18:30:00Z", null, null, null),
            new HouseholdRequestDto(2, "The Blues", "Sam Blue", "sam@example.com",
                HouseholdRequestStatus.Approved, "2026-06-20T09:15:00Z", "2026-06-21T10:00:00Z", "admin@example.com", null),
            new HouseholdRequestDto(1, "The Reds", "Alex Red", "alex@example.com",
                HouseholdRequestStatus.Rejected, "2026-06-18T12:00:00Z", "2026-06-19T08:30:00Z", "admin@example.com",
                "Duplicate of an existing household."),
        ],
        Households:
        [
            new HouseholdSummaryDto(1, "La Familia", 4, "2026-01-15T00:00:00Z"),
            new HouseholdSummaryDto(2, "The Blues", 1, "2026-06-21T10:00:00Z"),
        ]);

    public static FeedbackListDto BuildRepresentativeFeedback() => new(
        IsSiteAdmin: true,
        Items:
        [
            new FeedbackDto(5, FeedbackType.Bug, "The button doesn't work.", "/recipes",
                false, false, "2026-06-24T20:00:00Z", "Jamie", false),       // live user → name, !deleted
            new FeedbackDto(4, FeedbackType.FeatureRequest, "Please add dark mode.", null,
                true, false, "2026-06-23T14:30:00Z", null, true),            // deleted user → null + deleted
            new FeedbackDto(3, FeedbackType.General, "Love the app!", "/",
                true, true, "2026-06-22T08:00:00Z", null, false),            // anonymous → null + !deleted
        ]);

    [Fact]
    public void SerializedRequests_MatchesContractFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeRequests(), AdminJsonOptions);
        File.Exists(FixturePath("household-requests.json")).Should().BeTrue();
        Normalize(json).Should().Be(Normalize(File.ReadAllText(FixturePath("household-requests.json"))),
            "the serialized HouseholdRequestsDto must match household-requests.json; update the fixture AND types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedFeedback_MatchesContractFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeFeedback(), AdminJsonOptions);
        File.Exists(FixturePath("feedback.json")).Should().BeTrue();
        Normalize(json).Should().Be(Normalize(File.ReadAllText(FixturePath("feedback.json"))),
            "the serialized FeedbackListDto must match feedback.json; update the fixture AND types.ts in lockstep (M9)");
    }

    [Fact]
    public void Requests_UseCamelCaseKeys_EnumStrings_AndIsoInstantDates()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeRequests(), AdminJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("requests", "households");

        var pending = root["requests"]!.AsArray()[0]!.AsObject();
        pending.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "id", "householdName", "displayName", "email", "status",
            "requestedAt", "reviewedAt", "reviewedBy", "rejectionReason");
        // Enum → camelCase string (R-C10); date → full ISO-8601 instant with Z (X5).
        pending["status"]!.GetValue<string>().Should().Be("pending");
        pending["requestedAt"]!.GetValue<string>().Should().Be("2026-06-24T18:30:00Z");
        pending["reviewedAt"].Should().BeNull();

        var household = root["households"]!.AsArray()[0]!.AsObject();
        household.Select(kvp => kvp.Key).Should().BeEquivalentTo("householdId", "name", "memberCount", "createdAt");
    }

    [Fact]
    public void Feedback_UseCamelCaseKeys_EnumStrings_AndThreeWayAuthor()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeFeedback(), AdminJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("isSiteAdmin", "items");

        var items = root["items"]!.AsArray();

        var live = items[0]!.AsObject();
        live.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "id", "type", "message", "currentPage", "isRead", "isResolved", "createdAt", "authorName", "authorDeleted");
        live["type"]!.GetValue<string>().Should().Be("bug");
        live["authorName"]!.GetValue<string>().Should().Be("Jamie");
        live["authorDeleted"]!.GetValue<bool>().Should().BeFalse();

        // featureRequest enum serializes camelCase (R-C10).
        items[1]!["type"]!.GetValue<string>().Should().Be("featureRequest");
        // Deleted user → null name + deleted=true (R-C6).
        items[1]!["authorName"].Should().BeNull();
        items[1]!["authorDeleted"]!.GetValue<bool>().Should().BeTrue();

        // Anonymous → null name + deleted=false (R-C6).
        items[2]!["authorName"].Should().BeNull();
        items[2]!["authorDeleted"]!.GetValue<bool>().Should().BeFalse();
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
