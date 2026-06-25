using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the Settings island A aggregate DTOs (M9 — mirrors
/// <see cref="DashboardDtoContractTests"/>). Serializes a representative <see cref="CategoryListDto"/> /
/// <see cref="MemberListDto"/> with the SAME options the app registers globally (camelCase, Web defaults) and
/// asserts byte-equality with the checked-in <c>Fixtures/Settings/{categories,members}.json</c>. The island's
/// <c>types.ts</c> mirrors these fixtures; any DTO shape/casing change breaks this test → forcing the island
/// contract to update in lockstep (M9).
///
/// <para>⚠ DATES (review X5): <see cref="SettingsCategoryDto.DeletedAt"/> is a FULL INSTANT (DateTime?, UTC) →
/// serializes as a round-trip ISO-8601 string ("2026-06-20T14:30:00Z"), NOT a bare "YYYY-MM-DD". The island
/// renders it local via new Date(iso).</para>
/// </summary>
public class SettingsDtoContractTests
{
    /// <summary>Web defaults (camelCase) — equivalent to the app's global Minimal-API JSON config for these (enum-free) DTOs.</summary>
    public static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", file);

    public static CategoryListDto BuildRepresentativeCategories() => new(
        Active: new List<SettingsCategoryDto>
        {
            new(CategoryId: 1, Name: "Produce", IconEmoji: "leafy_green", Color: "#4CAF50", IsDefault: true, SortOrder: 1, DeletedAt: null),
            new(CategoryId: 2, Name: "Snacks", IconEmoji: null, Color: "#FF9800", IsDefault: false, SortOrder: 2, DeletedAt: null),
        },
        Deleted: new List<SettingsCategoryDto>
        {
            new(CategoryId: 3, Name: "Old Aisle", IconEmoji: null, Color: "#808080", IsDefault: false, SortOrder: 3,
                DeletedAt: new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc)),
        });

    public static MemberListDto BuildRepresentativeMembers() => new(
        CurrentUserId: 1,
        Members: new List<SettingsMemberDto>
        {
            new(UserId: 1, Email: "alice@example.com", DisplayName: "Alice", IsWhitelisted: true),
            new(UserId: 2, Email: "bob@example.com", DisplayName: null, IsWhitelisted: false),
        });

    [Fact]
    public void SerializedCategories_MatchesContractFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeCategories(), SettingsJsonOptions);
        File.Exists(FixturePath("categories.json")).Should().BeTrue();
        Normalize(json).Should().Be(Normalize(File.ReadAllText(FixturePath("categories.json"))),
            "the serialized CategoryListDto must match categories.json; update the fixture AND types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedMembers_MatchesContractFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeMembers(), SettingsJsonOptions);
        File.Exists(FixturePath("members.json")).Should().BeTrue();
        Normalize(json).Should().Be(Normalize(File.ReadAllText(FixturePath("members.json"))),
            "the serialized MemberListDto must match members.json; update the fixture AND types.ts in lockstep (M9)");
    }

    [Fact]
    public void Categories_UseCamelCaseKeys_AndIsoInstantDeletedAt()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeCategories(), SettingsJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("active", "deleted");

        var first = root["active"]!.AsArray()[0]!.AsObject();
        first.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "categoryId", "name", "iconEmoji", "color", "isDefault", "sortOrder", "deletedAt");
        first["deletedAt"].Should().BeNull("a JSON null property reads back as a null JsonNode");

        // DeletedAt is a full ISO-8601 instant (NOT a bare date) — review X5.
        var deleted = root["deleted"]!.AsArray()[0]!.AsObject();
        deleted["deletedAt"]!.GetValue<string>().Should().Be("2026-06-20T14:30:00Z");
    }

    [Fact]
    public void Members_UseCamelCaseKeys()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeMembers(), SettingsJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("currentUserId", "members");
        var first = root["members"]!.AsArray()[0]!.AsObject();
        first.Select(kvp => kvp.Key).Should().BeEquivalentTo("userId", "email", "displayName", "isWhitelisted");
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
