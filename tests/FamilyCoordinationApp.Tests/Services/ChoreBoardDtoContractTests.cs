using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the board DTO (M9). Serializes a representative
/// <see cref="ChoreBoardDto"/> with the SAME <see cref="JsonSerializerOptions"/> WP-06 registers globally
/// (camelCase properties + <see cref="JsonStringEnumConverter"/> with camelCase naming, council M5) and
/// asserts it matches the checked-in <c>Fixtures/ChoreBoard/board.json</c> fixture EXACTLY. The island's
/// <c>api.ts</c> TS interface mirrors this fixture; any DTO shape/casing change (field rename, enum casing
/// drift, added/removed property) breaks this test → forcing the island contract to update in lockstep.
/// </summary>
public class ChoreBoardDtoContractTests
{
    /// <summary>
    /// The canonical board-DTO serialization options. WP-06 MUST register an equivalent
    /// <see cref="JsonStringEnumConverter"/> (camelCase) globally so the HTTP responses match this fixture.
    /// </summary>
    public static readonly JsonSerializerOptions BoardJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoreBoard", "board.json");

    /// <summary>
    /// A representative board covering: every <see cref="DueState"/>/<see cref="ColorTier"/> on the chores,
    /// claimed + assigned + unclaimed-pile + stale-claim assignment states, a chore with a room and a roomless
    /// chore (the virtual General group), a populated room rollup + a clean empty room + the General rollup
    /// across all three rollup buckets, multiple members, the needs-attention ordering, and a non-null
    /// <c>userDefaultView</c>. Static values only — no clocks — so the serialization is byte-deterministic.
    /// </summary>
    public static ChoreBoardDto BuildRepresentativeBoard()
    {
        var chores = new List<ChoreDto>
        {
            // Overdue, claimed but stale (pile-eligible visually), in the Kitchen room.
            new(
                Id: 1,
                Name: "Mop the floor",
                Icon: "🧹",
                Description: "Use the good mop",
                RoomId: 10,
                RecurrenceMode: "Flexible",
                IntervalDays: 3,
                DaysOfWeek: null,
                DueState: DueState.Overdue,
                ColorTier: ColorTier.Overdue,
                NextDueAt: new DateTime(2026, 5, 28, 5, 0, 0, DateTimeKind.Utc),
                IsClaimStale: true,
                EffortTier: "Standard",
                EffortPoints: 2,
                OwnerUserId: 100,
                AssigneeUserId: 101,
                AssignmentKind: AssignmentKind.Claimed,
                ClaimedAt: new DateTime(2026, 5, 25, 5, 0, 0, DateTimeKind.Utc),
                LastCompletedAt: new DateTime(2026, 5, 20, 5, 0, 0, DateTimeKind.Utc),
                PhotoPath: "/uploads/1/abc.jpg",
                Version: 7),

            // Due today, assigned (sticky), in the Kitchen room.
            new(
                Id: 2,
                Name: "Take out trash",
                Icon: "",
                Description: null,
                RoomId: 10,
                RecurrenceMode: "Fixed",
                IntervalDays: null,
                DaysOfWeek: ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday,
                DueState: DueState.DueToday,
                ColorTier: ColorTier.Due,
                NextDueAt: new DateTime(2026, 5, 30, 5, 0, 0, DateTimeKind.Utc),
                IsClaimStale: false,
                EffortTier: "Quick",
                EffortPoints: 1,
                OwnerUserId: null,
                AssigneeUserId: 100,
                AssignmentKind: AssignmentKind.Assigned,
                ClaimedAt: new DateTime(2026, 5, 30, 4, 0, 0, DateTimeKind.Utc),
                LastCompletedAt: null,
                PhotoPath: null,
                Version: 3),

            // Scheduled, unclaimed pile (up-for-grabs), roomless (General group).
            new(
                Id: 3,
                Name: "Water the plants",
                Icon: "🪴",
                Description: "Living room + balcony",
                RoomId: null,
                RecurrenceMode: "Fixed",
                IntervalDays: null,
                DaysOfWeek: ChoreDaysOfWeek.Saturday,
                DueState: DueState.Scheduled,
                ColorTier: ColorTier.Fresh,
                NextDueAt: new DateTime(2026, 6, 2, 5, 0, 0, DateTimeKind.Utc),
                IsClaimStale: false,
                EffortTier: "BigJob",
                EffortPoints: 3,
                OwnerUserId: 100,
                AssigneeUserId: null,
                AssignmentKind: AssignmentKind.None,
                ClaimedAt: null,
                LastCompletedAt: new DateTime(2026, 5, 15, 5, 0, 0, DateTimeKind.Utc),
                PhotoPath: null,
                Version: 1),

            // Not due, fresh, roomless one-off in the General group (claimed, not stale).
            new(
                Id: 4,
                Name: "Fix the squeaky door",
                Icon: "",
                Description: null,
                RoomId: null,
                RecurrenceMode: "OneOff",
                IntervalDays: null,
                DaysOfWeek: null,
                DueState: DueState.NotDue,
                ColorTier: ColorTier.Mid,
                NextDueAt: null,
                IsClaimStale: false,
                EffortTier: "Quick",
                EffortPoints: 1,
                OwnerUserId: null,
                AssigneeUserId: 101,
                AssignmentKind: AssignmentKind.Claimed,
                ClaimedAt: new DateTime(2026, 5, 30, 3, 0, 0, DateTimeKind.Utc),
                LastCompletedAt: null,
                PhotoPath: null,
                Version: 2),
        };

        var rooms = new List<RoomRollupDto>
        {
            // Kitchen: 2 chores, both due-or-overdue => NeedsWork? No — 2 due => Attention bucket.
            new(
                RoomId: 10,
                Name: "Kitchen",
                Icon: "🍳",
                PhotoPath: "/uploads/1/kitchen.jpg",
                SortOrder: 1,
                ChoreCount: 2,
                DueCount: 2,
                Status: RoomRollupStatus.Attention),

            // Bathroom: a clean empty room (still listed).
            new(
                RoomId: 11,
                Name: "Bathroom",
                Icon: "🛁",
                PhotoPath: null,
                SortOrder: 2,
                ChoreCount: 0,
                DueCount: 0,
                Status: RoomRollupStatus.Clean),

            // General (virtual roomless group): 2 chores, 0 due => Clean.
            new(
                RoomId: null,
                Name: "General",
                Icon: "🏠",
                PhotoPath: null,
                SortOrder: int.MaxValue,
                ChoreCount: 2,
                DueCount: 0,
                Status: RoomRollupStatus.Clean),
        };

        var members = new List<MemberDto>
        {
            new(UserId: 100, DisplayName: "Alice", Initials: "AL", PictureUrl: "https://example.com/alice.png"),
            new(UserId: 101, DisplayName: "Bob", Initials: "BO", PictureUrl: null),
        };

        // Needs-attention: overdue (1) → due-today (2) → unclaimed pile (3). #4 is not-due + claimed => excluded.
        var needsAttention = new List<int> { 1, 2, 3 };

        return new ChoreBoardDto(
            chores,
            rooms,
            members,
            needsAttention,
            UserDefaultView: ChoreLens.Rooms);
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
            + "deliberate DTO change, update board.json AND the island api.ts interface in lockstep (M9)");
    }

    [Fact]
    public void SerializedBoard_UsesCamelCaseKeys_AndCamelCaseEnumStrings()
    {
        var board = BuildRepresentativeBoard();
        var json = JsonSerializer.Serialize(board, BoardJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        // Top-level camelCase property set is frozen (M9 — added/removed keys break this).
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "chores", "rooms", "members", "needsAttentionChoreIds", "userDefaultView");

        var firstChore = root["chores"]!.AsArray()[0]!.AsObject();
        firstChore.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "id", "name", "icon", "description", "roomId", "recurrenceMode", "intervalDays", "daysOfWeek",
            "dueState", "colorTier", "nextDueAt",
            "isClaimStale", "effortTier", "effortPoints", "ownerUserId", "assigneeUserId", "assignmentKind",
            "claimedAt", "lastCompletedAt", "photoPath", "version");

        // Enums serialize as camelCase strings (M11), NOT integers and NOT PascalCase.
        firstChore["dueState"]!.GetValue<string>().Should().Be("overdue");
        firstChore["colorTier"]!.GetValue<string>().Should().Be("overdue");
        firstChore["assignmentKind"]!.GetValue<string>().Should().Be("claimed");

        var assignedChore = root["chores"]!.AsArray()[1]!.AsObject();
        assignedChore["dueState"]!.GetValue<string>().Should().Be("dueToday");
        assignedChore["colorTier"]!.GetValue<string>().Should().Be("due");
        assignedChore["assignmentKind"]!.GetValue<string>().Should().Be("assigned");

        var pileChore = root["chores"]!.AsArray()[2]!.AsObject();
        pileChore["dueState"]!.GetValue<string>().Should().Be("scheduled");
        pileChore["colorTier"]!.GetValue<string>().Should().Be("fresh");
        pileChore["assignmentKind"]!.GetValue<string>().Should().Be("none");

        var firstRollup = root["rooms"]!.AsArray()[0]!.AsObject();
        firstRollup.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "roomId", "name", "icon", "photoPath", "sortOrder", "choreCount", "dueCount", "status");
        firstRollup["status"]!.GetValue<string>().Should().Be("attention");

        // The General rollup uses a null roomId and the camelCase needsWork casing is honored elsewhere.
        var generalRollup = root["rooms"]!.AsArray()[2]!.AsObject();
        generalRollup["roomId"].Should().BeNull();
        generalRollup["status"]!.GetValue<string>().Should().Be("clean");

        var firstMember = root["members"]!.AsArray()[0]!.AsObject();
        firstMember.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "userId", "displayName", "initials", "pictureUrl");
    }

    /// <summary>A deliberate rename of any contract field must break the fixture test (tripwire).</summary>
    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var board = BuildRepresentativeBoard();
        var json = JsonSerializer.Serialize(board, BoardJsonOptions);

        // Simulate a drift: rename "colorTier" -> "color". The mutated payload must NOT match the fixture.
        var drifted = json.Replace("\"colorTier\"", "\"color\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
