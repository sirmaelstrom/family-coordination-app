using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the meal-plan board DTO (M9 — mirrors <see cref="ChoreBoardDtoContractTests"/>).
/// Serializes a representative <see cref="MealPlanBoardDto"/> with the SAME <see cref="JsonSerializerOptions"/>
/// the app registers globally (camelCase properties + <see cref="JsonStringEnumConverter"/> with camelCase
/// naming — Program.cs ConfigureHttpJsonOptions) and asserts it matches the checked-in
/// <c>Fixtures/MealPlanBoard/board.json</c> fixture EXACTLY. The island's <c>types.ts</c> mirrors this fixture;
/// any DTO shape/casing change (field rename, enum casing drift, added/removed property) breaks this test →
/// forcing the island contract to update in lockstep.
/// </summary>
public class MealPlanBoardDtoContractTests
{
    /// <summary>
    /// The canonical board-DTO serialization options. The app's <c>ConfigureHttpJsonOptions</c> registers the
    /// equivalent camelCase <see cref="JsonStringEnumConverter"/> globally, so HTTP responses match this fixture.
    /// </summary>
    public static readonly JsonSerializerOptions BoardJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "MealPlanBoard", "board.json");

    /// <summary>
    /// A representative week board covering: a recipe entry WITH an image, a recipe entry WITHOUT an image (and
    /// with notes), a custom-meal entry, multiple meal types (breakfast/dinner/lunch), multiple days, and a
    /// non-null <c>mealPlanId</c>. Static values only — no clocks — so the serialization is byte-deterministic.
    /// </summary>
    public static MealPlanBoardDto BuildRepresentativeBoard()
    {
        var entries = new List<MealPlanEntryDto>
        {
            // Recipe entry WITH image, no notes — breakfast, day 1.
            new(
                MealPlanId: 5,
                EntryId: 1,
                Date: new DateOnly(2026, 6, 1),
                MealType: MealType.Breakfast,
                Recipe: new MealRecipeSummaryDto(10, "Overnight Oats", "/uploads/1/oats.jpg", RecipeType.Breakfast),
                CustomMealName: null,
                Notes: null),

            // Recipe entry WITHOUT image, with notes — dinner, day 1, a Main dish.
            new(
                MealPlanId: 5,
                EntryId: 2,
                Date: new DateOnly(2026, 6, 1),
                MealType: MealType.Dinner,
                Recipe: new MealRecipeSummaryDto(11, "Spaghetti Bolognese", null, RecipeType.Main),
                CustomMealName: null,
                Notes: "Double batch for leftovers"),

            // Custom meal entry — lunch, day 2.
            new(
                MealPlanId: 5,
                EntryId: 3,
                Date: new DateOnly(2026, 6, 2),
                MealType: MealType.Lunch,
                Recipe: null,
                CustomMealName: "Leftovers",
                Notes: "Sunday's roast"),
        };

        return new MealPlanBoardDto(
            WeekStartDate: new DateOnly(2026, 6, 1),
            MealPlanId: 5,
            Entries: entries);
    }

    [Fact]
    public void SerializedBoard_MatchesContractFixture()
    {
        var board = BuildRepresentativeBoard();

        var actualJson = JsonSerializer.Serialize(board, BoardJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the board.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        // Compare via parsed JSON nodes (whitespace/line-ending tolerant) so the assertion is about the
        // contract shape + values, not file formatting.
        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized board DTO must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update board.json AND the island types.ts interface in lockstep (M9)");
    }

    [Fact]
    public void SerializedBoard_UsesCamelCaseKeys_AndCamelCaseEnumStrings()
    {
        var board = BuildRepresentativeBoard();
        var json = JsonSerializer.Serialize(board, BoardJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        // Top-level camelCase property set is frozen (M9 — added/removed keys break this).
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "weekStartDate", "mealPlanId", "entries");

        // DateOnly serializes as "YYYY-MM-DD".
        root["weekStartDate"]!.GetValue<string>().Should().Be("2026-06-01");

        var firstEntry = root["entries"]!.AsArray()[0]!.AsObject();
        firstEntry.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "mealPlanId", "entryId", "date", "mealType", "recipe", "customMealName", "notes");

        // MealType serializes as a camelCase enum string (NOT an integer, NOT PascalCase).
        firstEntry["mealType"]!.GetValue<string>().Should().Be("breakfast");
        firstEntry["date"]!.GetValue<string>().Should().Be("2026-06-01");
        firstEntry["customMealName"].Should().BeNull();
        firstEntry["notes"].Should().BeNull();

        var firstRecipe = firstEntry["recipe"]!.AsObject();
        firstRecipe.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "recipeId", "name", "imagePath", "recipeType");
        // RecipeType is also a camelCase enum string.
        firstRecipe["recipeType"]!.GetValue<string>().Should().Be("breakfast");

        // Second entry: a recipe with a null image + a Main dish + notes.
        var secondEntry = root["entries"]!.AsArray()[1]!.AsObject();
        secondEntry["mealType"]!.GetValue<string>().Should().Be("dinner");
        secondEntry["recipe"]!.AsObject()["imagePath"].Should().BeNull();
        secondEntry["recipe"]!.AsObject()["recipeType"]!.GetValue<string>().Should().Be("main");
        secondEntry["notes"]!.GetValue<string>().Should().Be("Double batch for leftovers");

        // Third entry: a custom meal (recipe null, customMealName set).
        var thirdEntry = root["entries"]!.AsArray()[2]!.AsObject();
        thirdEntry["mealType"]!.GetValue<string>().Should().Be("lunch");
        thirdEntry["recipe"].Should().BeNull();
        thirdEntry["customMealName"]!.GetValue<string>().Should().Be("Leftovers");
    }

    /// <summary>A deliberate rename of any contract field must break the fixture test (tripwire).</summary>
    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var board = BuildRepresentativeBoard();
        var json = JsonSerializer.Serialize(board, BoardJsonOptions);

        // Simulate a drift: rename "mealType" -> "meal". The mutated payload must NOT match the fixture.
        var drifted = json.Replace("\"mealType\"", "\"meal\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
