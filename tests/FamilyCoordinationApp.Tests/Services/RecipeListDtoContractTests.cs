using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the recipes-island LIST DTO (M9 — mirrors
/// <see cref="MealPlanBoardDtoContractTests"/>). Serializes a representative <see cref="RecipeListDto"/> with the
/// SAME <see cref="JsonSerializerOptions"/> the app registers globally (camelCase properties +
/// <see cref="JsonStringEnumConverter"/> camelCase) and asserts it matches the checked-in
/// <c>Fixtures/RecipeList/list.json</c>. The island's <c>types.ts</c> mirrors this fixture; any DTO shape/casing
/// drift (rename, enum casing, added/removed field) breaks this test → forcing the island contract to update in
/// lockstep.
/// </summary>
public class RecipeListDtoContractTests
{
    public static readonly JsonSerializerOptions RecipeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RecipeList", "list.json");

    /// <summary>
    /// A representative grid payload covering: a card WITH image + source url + author + picture + ingredient
    /// preview; a card with NO image / NO source url / a DELETED author (null name+picture) but with a preview;
    /// a card with an author that has NO picture and NO ingredients (empty preview, count 0); plus a non-empty
    /// favoriteRecipeIds. Static values only — byte-deterministic.
    /// </summary>
    public static RecipeListDto BuildRepresentativeList()
    {
        var recipes = new List<RecipeListItemDto>
        {
            new(
                RecipeId: 10,
                Name: "Overnight Oats",
                RecipeType: RecipeType.Breakfast,
                ImagePath: "/uploads/1/oats.jpg",
                HasSourceUrl: true,
                CreatedByName: "Alice A",
                CreatedByPictureUrl: "https://pic.test/alice.jpg",
                IngredientPreview: new List<string> { "Rolled oats", "Milk", "Honey" },
                IngredientCount: 5),

            new(
                RecipeId: 11,
                Name: "Spaghetti Bolognese",
                RecipeType: RecipeType.Main,
                ImagePath: null,
                HasSourceUrl: false,
                CreatedByName: null,
                CreatedByPictureUrl: null,
                IngredientPreview: new List<string> { "Spaghetti", "Beef", "Tomato" },
                IngredientCount: 8),

            new(
                RecipeId: 12,
                Name: "Lemon Tart",
                RecipeType: RecipeType.Dessert,
                ImagePath: "/uploads/1/tart.jpg",
                HasSourceUrl: true,
                CreatedByName: "Amy A",
                CreatedByPictureUrl: null,
                IngredientPreview: new List<string>(),
                IngredientCount: 0),
        };

        return new RecipeListDto(recipes, new List<int> { 10, 12 });
    }

    [Fact]
    public void SerializedList_MatchesContractFixture()
    {
        var dto = BuildRepresentativeList();
        var actualJson = JsonSerializer.Serialize(dto, RecipeJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the list.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized RecipeListDto must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update list.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedList_UsesCamelCaseKeys_AndCamelCaseEnumStrings()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeList(), RecipeJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("recipes", "favoriteRecipeIds");

        var first = root["recipes"]!.AsArray()[0]!.AsObject();
        first.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "recipeId", "name", "recipeType", "imagePath", "hasSourceUrl",
            "createdByName", "createdByPictureUrl", "ingredientPreview", "ingredientCount");

        // RecipeType is a camelCase enum string (NOT an integer, NOT PascalCase).
        first["recipeType"]!.GetValue<string>().Should().Be("breakfast");
        first["hasSourceUrl"]!.GetValue<bool>().Should().BeTrue();

        // Deleted-author card: null name + null picture.
        var second = root["recipes"]!.AsArray()[1]!.AsObject();
        second["recipeType"]!.GetValue<string>().Should().Be("main");
        second["imagePath"].Should().BeNull();
        second["createdByName"].Should().BeNull();
        second["createdByPictureUrl"].Should().BeNull();

        root["favoriteRecipeIds"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(10, 12);
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeList(), RecipeJsonOptions);
        var drifted = json.Replace("\"recipeType\"", "\"type\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
