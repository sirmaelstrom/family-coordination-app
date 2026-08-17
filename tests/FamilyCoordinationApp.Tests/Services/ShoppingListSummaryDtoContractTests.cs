using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract tests for the shopping-list SUMMARY payload (M9 pin v2 — mirrors
/// <see cref="ShoppingListDtoContractTests"/>). <c>GET /api/shopping-list</c> answers a bare array of
/// <see cref="ShoppingListEndpoints.ShoppingListSummaryDto"/>; the fixture pins that array shape.
/// </summary>
public class ShoppingListSummaryDtoContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ShoppingList", "summaries.json");

    /// <summary>Two summaries covering both isFavorite states and a fully-checked list (uncheckedCount 0).</summary>
    public static List<ShoppingListEndpoints.ShoppingListSummaryDto> BuildRepresentativeSummaries() =>
    [
        new(Id: 4, Name: "Weekly Shop", IsFavorite: true, ItemCount: 12, UncheckedCount: 5),
        new(Id: 9, Name: "Hardware Store", IsFavorite: false, ItemCount: 3, UncheckedCount: 0),
    ];

    [Fact]
    public void SerializedSummaries_MatchContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeSummaries(), Options);

        File.Exists(FixturePath).Should().BeTrue(
            $"the summaries.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized summary array must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update summaries.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeSummaries(), Options);
        var drifted = json.Replace("\"uncheckedCount\"", "\"remaining\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
