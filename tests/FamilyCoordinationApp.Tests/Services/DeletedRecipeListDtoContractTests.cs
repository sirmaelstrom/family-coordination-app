using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract test for the Deleted Recipes list (M9, mirrors <see cref="RecipeListDtoContractTests"/>): a
/// representative <see cref="DeletedRecipeListDto"/>, serialized with the app's JSON options, must match the
/// checked-in <c>Fixtures/RecipeList/deleted.json</c>. The SPA's <c>settings/lib/types.ts</c> mirrors that fixture
/// (pinned in <c>#lib/api/contracts.ts</c>), so a DTO rename fails here and then there.
/// </summary>
public class DeletedRecipeListDtoContractTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RecipeList", "deleted.json");

    /// <summary>A stamped delete (a full UTC instant) and an unstamped one (null). Static values only.</summary>
    public static DeletedRecipeListDto BuildRepresentativeList() => new(new List<DeletedRecipeDto>
    {
        new(RecipeId: 21, Name: "Old Chili", RecipeType: RecipeType.Main,
            DeletedAt: new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc)),
        new(RecipeId: 7, Name: "First Pancakes", RecipeType: RecipeType.Breakfast, DeletedAt: null),
    });

    [Fact]
    public void SerializedList_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeList(), RecipeListDtoContractTests.RecipeJsonOptions);

        File.Exists(FixturePath).Should().BeTrue($"the deleted.json contract fixture must be checked in at {FixturePath}");
        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized DeletedRecipeListDto must match the checked-in fixture; after a deliberate DTO change, "
            + "update deleted.json AND the SPA's settings/lib/types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedList_UsesCamelCaseKeys_AnEnumString_AndAFullInstant()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeList(), RecipeListDtoContractTests.RecipeJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("recipes");
        var first = root["recipes"]!.AsArray()[0]!.AsObject();
        first.Select(kvp => kvp.Key).Should().BeEquivalentTo("recipeId", "name", "recipeType", "deletedAt");
        first["recipeType"]!.GetValue<string>().Should().Be("main");
        // A full ISO-8601 instant, never a bare date: the SPA parses it with new Date(iso).
        first["deletedAt"]!.GetValue<string>().Should().Be("2026-06-20T14:30:00Z");
        root["recipes"]!.AsArray()[1]!["deletedAt"].Should().BeNull();
    }

    /// <summary>The one projection: <c>deletedAt</c> is the recipe row's <c>UpdatedAt</c>, which the delete stamps.</summary>
    [Fact]
    public void ToDeleted_MapsTheRow_AndTakesDeletedAtFromUpdatedAt()
    {
        var deletedAt = new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc);
        var recipe = new Recipe
        {
            HouseholdId = 1, RecipeId = 21, Name = "Old Chili", RecipeType = RecipeType.Main,
            IsDeleted = true, CreatedAt = deletedAt.AddDays(-30), UpdatedAt = deletedAt,
        };

        new RecipeProjectionService().ToDeleted(recipe)
            .Should().Be(new DeletedRecipeDto(21, "Old Chili", RecipeType.Main, deletedAt));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
