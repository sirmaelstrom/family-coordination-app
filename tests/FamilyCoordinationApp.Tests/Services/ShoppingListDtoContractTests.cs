using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the shopping-list read DTO (M9 — mirrors
/// <see cref="SettingsDtoContractTests"/>). Serializes a representative
/// <see cref="ShoppingListEndpoints.ShoppingListDto"/> with the SAME options the app registers globally and
/// asserts byte-equality with the checked-in <c>Fixtures/ShoppingList/list.json</c>. The island's
/// <c>types.ts</c> mirrors that fixture; any DTO shape/casing change breaks this test → forcing the island
/// contract to update in lockstep (M9).
///
/// <para>⚠ DATES (review X5): <c>CheckedAt</c> is a FULL INSTANT (DateTime?, UTC) → serializes as a
/// round-trip ISO-8601 string, NOT a bare "YYYY-MM-DD".</para>
/// </summary>
public class ShoppingListDtoContractTests
{
    /// <summary>Web defaults (camelCase) — equivalent to the app's global Minimal-API JSON config for these (enum-free) DTOs.</summary>
    public static readonly JsonSerializerOptions ShoppingListJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ShoppingList", "list.json");

    /// <summary>
    /// A representative list covering every nullable the island's types.ts declares: an unchecked item with a
    /// fractional quantity, a unit and a full author identity; a checked item (instant + null quantity/unit)
    /// whose author has no picture; and an item whose author is gone (all three addedBy* null). Static values
    /// only — byte-deterministic.
    /// </summary>
    public static ShoppingListEndpoints.ShoppingListDto BuildRepresentativeList() => new(
        Id: 4,
        Name: "Weekly Shop",
        IsFavorite: true,
        IsArchived: false,
        Items: new List<ShoppingListEndpoints.ShoppingListItemDto>
        {
            new(Id: 1, Name: "Bananas", Quantity: 2.5m, Unit: "lb", Category: "Produce",
                IsChecked: false, CheckedAt: null, SortOrder: 1,
                AddedByName: "Alice", AddedByInitials: "AA",
                AddedByPictureUrl: "https://pic.test/alice.jpg", Version: 7),

            new(Id: 2, Name: "Milk", Quantity: null, Unit: null, Category: "Dairy",
                IsChecked: true, CheckedAt: new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc), SortOrder: 2,
                AddedByName: "Bob", AddedByInitials: "BB", AddedByPictureUrl: null, Version: 3),

            new(Id: 3, Name: "Paper towels", Quantity: 1m, Unit: null, Category: "Household",
                IsChecked: false, CheckedAt: null, SortOrder: 3,
                AddedByName: null, AddedByInitials: null, AddedByPictureUrl: null, Version: 1),
        });

    [Fact]
    public void SerializedList_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeList(), ShoppingListJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the list.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized ShoppingListDto must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update list.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void SerializedList_UsesCamelCaseKeys_AndPreservesNulls()
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(BuildRepresentativeList(), ShoppingListJsonOptions))!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("id", "name", "isFavorite", "isArchived", "items");

        var items = root["items"]!.AsArray();
        items[0]!.AsObject().Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "id", "name", "quantity", "unit", "category", "isChecked", "checkedAt", "sortOrder",
            "addedByName", "addedByInitials", "addedByPictureUrl", "version");

        // A null field stays PRESENT as null — the island's `T | null` types read the key, never `undefined`.
        items[0]!["checkedAt"].Should().BeNull();
        items[1]!["quantity"].Should().BeNull();
        items[2]!["addedByName"].Should().BeNull();

        items[0]!["quantity"]!.GetValue<decimal>().Should().Be(2.5m);
        items[1]!["checkedAt"]!.GetValue<DateTime>().Should().Be(new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeList(), ShoppingListJsonOptions);
        var drifted = json.Replace("\"isChecked\"", "\"checked\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
