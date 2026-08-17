using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract tests for the two recipe WRITE bodies (M9 pin v2 — the first request-direction pins).
/// The server DESERIALIZES these; serializing a representative instance with the app's global options
/// yields the same wire shape the binder expects, so the fixture pins what the SPA must send:
/// <c>Fixtures/RecipeWrite/request.json</c> for <see cref="RecipesEndpoints.RecipeWriteRequest"/>
/// (POST/PUT /api/recipes) and <c>Fixtures/RecipeDraft/save-request.json</c> for
/// <see cref="RecipesEndpoints.SaveDraftRequest"/> (PUT /api/recipes/draft).
/// </summary>
public class RecipeWriteContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string WriteFixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RecipeWrite", "request.json");

    private static readonly string DraftFixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RecipeDraft", "save-request.json");

    /// <summary>Covers the enum field, a fractional quantity, both null-and-set on every nullable pair, and a Version.</summary>
    public static RecipesEndpoints.RecipeWriteRequest BuildRepresentativeWriteRequest() => new(
        Name: "Pancakes",
        Description: "Fluffy breakfast pancakes",
        Instructions: "Mix and fry.",
        SourceUrl: null,
        Servings: 4,
        PrepTimeMinutes: 10,
        CookTimeMinutes: null,
        RecipeType: RecipeType.Breakfast,
        ImagePath: null,
        Ingredients: new List<RecipesEndpoints.RecipeIngredientWrite>
        {
            new(Name: "flour", Quantity: 2.5m, Unit: "cup", Category: "Baking",
                Notes: null, GroupName: null, SortOrder: 1),
            new(Name: "eggs", Quantity: null, Unit: null, Category: "Dairy",
                Notes: "room temperature", GroupName: "Wet", SortOrder: 2),
        },
        Version: 7);

    /// <summary>A new-recipe draft (recipeId null) with a stored image path.</summary>
    public static RecipesEndpoints.SaveDraftRequest BuildRepresentativeDraftRequest() => new(
        RecipeId: null,
        Name: "Untitled soup",
        Description: null,
        Instructions: "Simmer until done.",
        ImagePath: "/uploads/1/draft.jpg",
        SourceUrl: null,
        Servings: null,
        PrepTimeMinutes: 5,
        CookTimeMinutes: 30,
        Ingredients: new List<RecipesEndpoints.DraftIngredientBody>
        {
            new(Name: "stock", Quantity: 1.5m, Unit: "l", Category: "Pantry",
                Notes: null, GroupName: null, SortOrder: 1),
        });

    [Fact]
    public void SerializedWriteRequest_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeWriteRequest(), Options);

        File.Exists(WriteFixturePath).Should().BeTrue(
            $"the request.json contract fixture must be checked in at {WriteFixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(WriteFixturePath)),
            "the serialized RecipeWriteRequest must match the checked-in contract fixture; if this fails "
            + "after a deliberate change, update request.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedDraftRequest_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeDraftRequest(), Options);

        File.Exists(DraftFixturePath).Should().BeTrue(
            $"the save-request.json contract fixture must be checked in at {DraftFixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(DraftFixturePath)),
            "the serialized SaveDraftRequest must match the checked-in contract fixture; if this fails "
            + "after a deliberate change, update save-request.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeWriteRequest(), Options);
        var drifted = json.Replace("\"recipeType\"", "\"type\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(WriteFixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
