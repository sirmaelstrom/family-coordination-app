using System.Text;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Calendar;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

public sealed class CalendarWriterTests
{
    private readonly CalendarWriter _writer = new();

    [Fact]
    public void WriteMealPlan_EscapesDescription_UsesCrLf_AndMakesAllDayEndExclusive()
    {
        var entry = Entry(new DateOnly(2026, 9, 7), notes: "comma, semicolon; slash\\ line one\nline two");

        var calendar = _writer.WriteMealPlan(7, new DateOnly(2026, 9, 7), [entry], new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        calendar.Should().Contain("DESCRIPTION:comma\\, semicolon\\; slash\\\\ line one\\nline two\r\n");
        calendar.Should().Contain("DTSTART;VALUE=DATE:20260907\r\n");
        calendar.Should().Contain("DTEND;VALUE=DATE:20260908\r\n");
        calendar.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n");
        calendar.Should().EndWith("END:VCALENDAR\r\n");
    }

    [Fact]
    public void WriteMealPlan_FoldsLongUtf8LinesAt75Octets_WithContinuationSpace()
    {
        var calendar = _writer.WriteMealPlan(7, new DateOnly(2026, 9, 7), [Entry(notes: new string('é', 100))], DateTimeOffset.UnixEpoch);
        var physicalLines = calendar.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        physicalLines.Should().Contain(line => line.StartsWith("DESCRIPTION:", StringComparison.Ordinal));
        physicalLines.Should().Contain(line => line.StartsWith(' '));
        physicalLines.Should().OnlyContain(line => Encoding.UTF8.GetByteCount(line) <= 75);
    }

    [Fact]
    public void WriteMealPlan_EmptyWindow_UsesOneAllDayPlaceholderEvent()
    {
        var calendar = _writer.WriteMealPlan(7, new DateOnly(2026, 9, 7), [], DateTimeOffset.UnixEpoch);

        calendar.Split("BEGIN:VEVENT", StringSplitOptions.None).Length.Should().Be(2);
        calendar.Should().Contain("SUMMARY:No meals planned\r\n");
        calendar.Should().Contain("UID:mealplan-7-empty@family-coordination-app\r\n");
        calendar.Should().Contain("DTSTART;VALUE=DATE:20260907\r\n");
        calendar.Should().Contain("DTEND;VALUE=DATE:20260908\r\n");
    }

    [Fact]
    public void WriteMealPlan_NonEmptyWindow_HasNoPlaceholderEvent()
    {
        var calendar = _writer.WriteMealPlan(7, new DateOnly(2026, 9, 7), [Entry()], DateTimeOffset.UnixEpoch);

        calendar.Should().NotContain("No meals planned");
        calendar.Should().NotContain("mealplan-7-empty@family-coordination-app");
    }

    private static MealPlanEntryDto Entry(DateOnly? date = null, string? notes = null) => new(
        MealPlanId: 11,
        EntryId: 12,
        Date: date ?? new DateOnly(2026, 9, 7),
        MealType: MealType.Dinner,
        Recipe: new MealRecipeSummaryDto(13, "Supper", null, RecipeType.Main, null),
        CustomMealName: null,
        Notes: notes,
        Servings: 4,
        Version: 9);
}
