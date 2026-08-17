using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract test for the category WRITE body (M9 pin v2 — request direction, mirrors
/// <see cref="RecipeWriteContractTests"/>). Pins <see cref="CategoryWriteRequest"/>
/// (POST/PUT /api/settings/categories) to <c>Fixtures/Settings/category-write.json</c>.
/// </summary>
public class CategoryWriteRequestContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", "category-write.json");

    public static CategoryWriteRequest BuildRepresentativeRequest() =>
        new(Name: "Produce", IconEmoji: "🥦", Color: "#22c55e");

    [Fact]
    public void SerializedRequest_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeRequest(), Options);

        File.Exists(FixturePath).Should().BeTrue(
            $"the category-write.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized CategoryWriteRequest must match the checked-in contract fixture; if this fails "
            + "after a deliberate change, update category-write.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeRequest(), Options);
        var drifted = json.Replace("\"iconEmoji\"", "\"emoji\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
