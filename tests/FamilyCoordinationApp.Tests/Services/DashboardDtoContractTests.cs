using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the dashboard aggregate DTO (M9 — mirrors
/// <see cref="MealPlanBoardDtoContractTests"/>). Serializes a representative <see cref="DashboardDto"/> with the
/// SAME <see cref="JsonSerializerOptions"/> the app registers globally (camelCase properties +
/// <see cref="JsonStringEnumConverter"/> with camelCase naming — Program.cs ConfigureHttpJsonOptions) and asserts
/// it matches the checked-in <c>Fixtures/Dashboard/dashboard.json</c> fixture EXACTLY. The island's <c>types.ts</c>
/// mirrors this fixture; any DTO shape/casing change (field rename, enum casing drift, added/removed property)
/// breaks this test → forcing the island contract to update in lockstep (M9).
/// </summary>
public class DashboardDtoContractTests
{
    /// <summary>The canonical dashboard-DTO serialization options — equivalent to the app's global config.</summary>
    public static readonly JsonSerializerOptions DashboardJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dashboard", "dashboard.json");

    /// <summary>
    /// A representative dashboard covering: a greeting + household name, an echoed <c>today</c>, the four chore
    /// counts (all non-zero), a shopping summary mid-progress (checked + remaining), and today's meals spanning
    /// multiple meal types incl. a recipe name, a custom-meal name, and the "Unnamed meal" placeholder (the
    /// deleted-recipe edge). Static values only — no clocks — so the serialization is byte-deterministic.
    /// </summary>
    public static DashboardDto BuildRepresentativeDashboard() => new(
        GreetingName: "Alex",
        HouseholdName: "The Smiths",
        Today: new DateOnly(2026, 6, 24),
        Chores: new DashboardChoreSummaryDto(ActiveTotal: 7, Overdue: 2, DueToday: 1, UpForGrabs: 3),
        Shopping: new DashboardShoppingSummaryDto(Remaining: 5, Checked: 3, Total: 8),
        TodaysMeals: new List<DashboardMealDto>
        {
            new(MealType.Breakfast, "Overnight Oats"),   // recipe name
            new(MealType.Lunch, "Leftovers"),            // custom-meal name
            new(MealType.Dinner, "Unnamed meal"),        // deleted-recipe placeholder
        });

    [Fact]
    public void SerializedDashboard_MatchesContractFixture()
    {
        var dashboard = BuildRepresentativeDashboard();

        var actualJson = JsonSerializer.Serialize(dashboard, DashboardJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the dashboard.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized dashboard DTO must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update dashboard.json AND the island types.ts interface in lockstep (M9)");
    }

    [Fact]
    public void SerializedDashboard_UsesCamelCaseKeys_AndCamelCaseEnumStrings()
    {
        var dashboard = BuildRepresentativeDashboard();
        var json = JsonSerializer.Serialize(dashboard, DashboardJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        // Top-level camelCase property set is frozen (M9 — added/removed keys break this).
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "greetingName", "householdName", "today", "chores", "shopping", "todaysMeals");

        // DateOnly serializes as "YYYY-MM-DD".
        root["today"]!.GetValue<string>().Should().Be("2026-06-24");

        var chores = root["chores"]!.AsObject();
        chores.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "activeTotal", "overdue", "dueToday", "upForGrabs");
        chores["activeTotal"]!.GetValue<int>().Should().Be(7);

        var shopping = root["shopping"]!.AsObject();
        shopping.Select(kvp => kvp.Key).Should().BeEquivalentTo("remaining", "checked", "total");
        shopping["total"]!.GetValue<int>().Should().Be(8);

        var meals = root["todaysMeals"]!.AsArray();
        meals.Count.Should().Be(3);
        var firstMeal = meals[0]!.AsObject();
        firstMeal.Select(kvp => kvp.Key).Should().BeEquivalentTo("mealType", "displayName");
        // MealType serializes as a camelCase enum string (NOT an integer, NOT PascalCase).
        firstMeal["mealType"]!.GetValue<string>().Should().Be("breakfast");
        firstMeal["displayName"]!.GetValue<string>().Should().Be("Overnight Oats");
        meals[1]!.AsObject()["mealType"]!.GetValue<string>().Should().Be("lunch");
        meals[2]!.AsObject()["displayName"]!.GetValue<string>().Should().Be("Unnamed meal");
    }

    /// <summary>A deliberate rename of any contract field must break the fixture test (tripwire).</summary>
    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var dashboard = BuildRepresentativeDashboard();
        var json = JsonSerializer.Serialize(dashboard, DashboardJsonOptions);

        // Simulate a drift: rename "upForGrabs" -> "grabbable". The mutated payload must NOT match the fixture.
        var drifted = json.Replace("\"upForGrabs\"", "\"grabbable\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
