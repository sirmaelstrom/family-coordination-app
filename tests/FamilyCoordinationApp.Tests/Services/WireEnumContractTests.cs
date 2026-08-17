using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Pins every wire-reaching enum's FULL member list to <c>Fixtures/Enums/wire-enums.json</c> (M9 pin v2).
/// The per-DTO fixtures can only ever contain the members their representative instances use, and the SPA's
/// oneOf shapes check membership, not list-equality — so a NEW enum member would escape both. This test
/// closes that hole: it serializes <c>Enum.GetValues</c> for each wire enum, in the exact casing that enum
/// reaches the wire in, and byte-compares the result to the checked-in fixture. The SPA's contracts.test.ts
/// asserts the same fixture list-equals its WIRE_ENUMS — so a new member fails HERE until the fixture grows,
/// and the grown fixture fails npm test until the TS union (and the island type it feeds) grows too.
///
/// <para>Two casing groups, matching how each field actually reaches the wire: enum-TYPED DTO fields go
/// through the app's global <c>JsonStringEnumConverter(CamelCase)</c>; <c>RecurrenceMode</c> and
/// <c>EffortTier</c> ride as string fields written by <c>Enum.ToString()</c> in ChoreBoardService
/// (PascalCase). NOT here: <c>callerCapacityTier</c> (User.PhysicalCapacityTier is a string column, no enum
/// to pin — A9 territory) and the goneQuiet/ghost <c>reason</c> (a documented string field).</para>
/// </summary>
public class WireEnumContractTests
{
    private static readonly JsonSerializerOptions CamelCaseEnumOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Enums", "wire-enums.json");

    private static string[] CamelMembers<T>() where T : struct, Enum =>
        Enum.GetValues<T>()
            .Select(v => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(v, CamelCaseEnumOptions))!)
            .ToArray();

    private static string[] ToStringMembers<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => v.ToString()).ToArray();

    /// <summary>Insertion order mirrors the SPA's WIRE_ENUMS so the two carriers read alike.</summary>
    public static Dictionary<string, string[]> BuildWireEnumVocabulary() => new()
    {
        ["RecipeType"] = CamelMembers<RecipeType>(),
        ["MealType"] = CamelMembers<MealType>(),
        ["DueState"] = CamelMembers<DueState>(),
        ["ColorTier"] = CamelMembers<ColorTier>(),
        ["AssignmentKind"] = CamelMembers<AssignmentKind>(),
        ["RosterState"] = CamelMembers<RosterState>(),
        ["RoomRollupStatus"] = CamelMembers<RoomRollupStatus>(),
        ["EquityWindow"] = CamelMembers<EquityWindow>(),
        ["FeedbackType"] = CamelMembers<FeedbackType>(),
        ["HouseholdRequestStatus"] = CamelMembers<HouseholdRequestStatus>(),
        ["DigestCadence"] = CamelMembers<DigestCadence>(),
        ["DayOfWeek"] = CamelMembers<DayOfWeek>(),
        // String-typed wire fields carrying Enum.ToString() (ChoreBoardService), hence PascalCase.
        ["RecurrenceMode"] = ToStringMembers<RecurrenceMode>(),
        ["EffortTier"] = ToStringMembers<EffortTier>(),
    };

    [Fact]
    public void SerializedVocabulary_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(
            BuildWireEnumVocabulary(), new JsonSerializerOptions { WriteIndented = true });

        File.Exists(FixturePath).Should().BeTrue(
            $"the wire-enums.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "every wire enum's Enum.GetValues must match the checked-in vocabulary; if this fails after "
            + "adding an enum member, update wire-enums.json AND the SPA's WIRE_ENUMS + island union in "
            + "lockstep (M9 pin v2)");
    }

    [Fact]
    public void AddingAnEnumMember_BreaksTheFixture()
    {
        // The mutation this pin exists to catch: one extra member in any list.
        var drifted = BuildWireEnumVocabulary();
        drifted["RecipeType"] = drifted["RecipeType"].Append("soup").ToArray();

        var driftedJson = JsonSerializer.Serialize(drifted, new JsonSerializerOptions { WriteIndented = true });

        Normalize(driftedJson).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
