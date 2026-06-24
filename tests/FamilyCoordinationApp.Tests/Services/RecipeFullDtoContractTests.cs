using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the recipes-island FULL DTO (read drawer + edit form superset, M9).
/// Serializes a representative <see cref="RecipeFullDto"/> with the global camelCase + enum-string options and
/// asserts it matches the checked-in <c>Fixtures/RecipeFull/recipe.json</c>. Locks the edit/drawer superset
/// shape — both raw <c>instructions</c> (markdown for the textarea) AND <c>instructionsHtml</c> (sanitized for
/// the drawer), plus per-ingredient <c>category</c>/<c>groupName</c>/<c>ingredientId</c>.
/// </summary>
public class RecipeFullDtoContractTests
{
    private static readonly JsonSerializerOptions RecipeJsonOptions = RecipeListDtoContractTests.RecipeJsonOptions;

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RecipeFull", "recipe.json");

    /// <summary>
    /// A representative full recipe: raw + sanitized instructions, a null cook time, an author with a picture,
    /// and three ingredients exercising decimal quantity (0.5), whole quantity (1), null quantity/unit, a
    /// non-default category, and an optional notes + groupName. Static values only.
    /// </summary>
    public static RecipeFullDto BuildRepresentativeFull()
    {
        var ingredients = new List<RecipeIngredientFullDto>
        {
            new(IngredientId: 1, Quantity: 0.5m, Unit: "cup", Name: "Rolled oats",
                Category: "Pantry", Notes: null, GroupName: null, SortOrder: 0),
            new(IngredientId: 2, Quantity: 1m, Unit: "cup", Name: "Milk",
                Category: "Dairy", Notes: "any kind", GroupName: "For the base", SortOrder: 1),
            new(IngredientId: 3, Quantity: null, Unit: null, Name: "Pinch of salt",
                Category: "Pantry", Notes: null, GroupName: null, SortOrder: 2),
        };

        return new RecipeFullDto(
            RecipeId: 10,
            Name: "Overnight Oats",
            RecipeType: RecipeType.Breakfast,
            Description: "Creamy make-ahead oats.",
            Instructions: "1. Combine oats and milk.\n2. Chill overnight.",
            InstructionsHtml: "<ol><li>Combine oats and milk.</li><li>Chill overnight.</li></ol>",
            ImagePath: "/uploads/1/oats.jpg",
            SourceUrl: "https://example.test/oats",
            PrepTimeMinutes: 10,
            CookTimeMinutes: null,
            Servings: 4,
            CreatedByName: "Alice A",
            CreatedByPictureUrl: "https://pic.test/alice.jpg",
            SharedFromHouseholdName: null,
            Ingredients: ingredients);
    }

    [Fact]
    public void SerializedFull_MatchesContractFixture()
    {
        var dto = BuildRepresentativeFull();
        var actualJson = JsonSerializer.Serialize(dto, RecipeJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the recipe.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized RecipeFullDto must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update recipe.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedFull_HasBothRawAndSanitizedInstructions_AndCamelCaseEnums()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeFull(), RecipeJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "recipeId", "name", "recipeType", "description", "instructions", "instructionsHtml",
            "imagePath", "sourceUrl", "prepTimeMinutes", "cookTimeMinutes", "servings",
            "createdByName", "createdByPictureUrl", "sharedFromHouseholdName", "ingredients");

        root["recipeType"]!.GetValue<string>().Should().Be("breakfast");
        root["instructions"]!.GetValue<string>().Should().Contain("Combine oats");   // raw markdown
        root["instructionsHtml"]!.GetValue<string>().Should().StartWith("<ol>");      // sanitized html
        root["cookTimeMinutes"].Should().BeNull();

        var ing = root["ingredients"]!.AsArray()[0]!.AsObject();
        ing.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "ingredientId", "quantity", "unit", "name", "category", "notes", "groupName", "sortOrder");
        ing["quantity"]!.GetValue<decimal>().Should().Be(0.5m);

        // A null-quantity ingredient serializes quantity:null (not 0).
        root["ingredients"]!.AsArray()[2]!.AsObject()["quantity"].Should().BeNull();
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeFull(), RecipeJsonOptions);
        var drifted = json.Replace("\"instructionsHtml\"", "\"html\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
