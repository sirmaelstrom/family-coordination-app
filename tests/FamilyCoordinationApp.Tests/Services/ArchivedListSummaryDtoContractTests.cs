using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract tests for the past-lists browse payload (quest f63bb90a — mirrors
/// <see cref="ShoppingListSummaryDtoContractTests"/>). <c>GET /api/shopping-lists/archived</c> answers a
/// bare array of <see cref="ShoppingListEndpoints.ArchivedListSummaryDto"/>; the fixture pins that shape.
/// <para>⚠ DATES: <c>CreatedAt</c> is a FULL INSTANT (UTC) → round-trip ISO-8601, never a bare date.</para>
/// </summary>
public class ArchivedListSummaryDtoContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ShoppingList", "archived-summaries.json");

    /// <summary>Both HasMealPlan states, both favorite states, and a fully-checked list (uncheckedCount 0).</summary>
    public static List<ShoppingListEndpoints.ArchivedListSummaryDto> BuildRepresentativeSummaries() =>
    [
        new(Id: 3, Name: "Holiday Prep", IsFavorite: true, ItemCount: 20, UncheckedCount: 0,
            CreatedAt: new DateTime(2026, 5, 30, 18, 0, 0, DateTimeKind.Utc), HasMealPlan: true),
        new(Id: 2, Name: "One-off Hardware Run", IsFavorite: false, ItemCount: 4, UncheckedCount: 1,
            CreatedAt: new DateTime(2026, 6, 10, 9, 15, 0, DateTimeKind.Utc), HasMealPlan: false),
    ];

    [Fact]
    public void SerializedSummaries_MatchContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeSummaries(), Options);

        File.Exists(FixturePath).Should().BeTrue(
            $"the archived-summaries.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized archived-summary array must match the checked-in contract fixture; if this fails "
            + "after a deliberate DTO change, update archived-summaries.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeSummaries(), Options);
        var drifted = json.Replace("\"hasMealPlan\"", "\"linked\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
