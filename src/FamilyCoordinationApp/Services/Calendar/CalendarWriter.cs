using System.Text;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Calendar;

/// <summary>Small RFC 5545 writer for the all-day meal-plan feed.</summary>
public sealed class CalendarWriter : ICalendarWriter
{
    private const string CrLf = "\r\n";

    public string WriteMealPlan(int householdId, DateOnly windowStart, IEnumerable<MealPlanEntryDto> entries, DateTimeOffset nowUtc)
    {
        var builder = new StringBuilder();
        Append(builder, "BEGIN:VCALENDAR");
        Append(builder, "PRODID:-//Family Coordination App//Meal Plan//EN");
        Append(builder, "VERSION:2.0");
        Append(builder, "METHOD:PUBLISH");
        Append(builder, "X-WR-CALNAME:Meal Plan");

        var orderedEntries = entries.OrderBy(entry => entry.Date).ThenBy(entry => entry.MealType).ToList();
        if (orderedEntries.Count == 0)
        {
            AppendPlaceholder(builder, householdId, windowStart, nowUtc);
        }

        foreach (var entry in orderedEntries)
        {
            var mealName = entry.Recipe?.Name ?? entry.CustomMealName ?? string.Empty;
            var servings = entry.Servings.HasValue ? $" (×{entry.Servings.Value})" : string.Empty;
            var end = entry.Date.AddDays(1);

            Append(builder, "BEGIN:VEVENT");
            Append(builder, $"UID:mealplan-{householdId}-{entry.MealPlanId}-{entry.EntryId}@family-coordination-app");
            Append(builder, $"DTSTAMP:{nowUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
            Append(builder, $"DTSTART;VALUE=DATE:{entry.Date:yyyyMMdd}");
            Append(builder, $"DTEND;VALUE=DATE:{end:yyyyMMdd}");
            Append(builder, $"SUMMARY:{Escape($"{entry.MealType}: {mealName}{servings}")}");
            if (!string.IsNullOrEmpty(entry.Notes))
            {
                Append(builder, $"DESCRIPTION:{Escape(entry.Notes)}");
            }
            Append(builder, "END:VEVENT");
        }

        Append(builder, "END:VCALENDAR");
        return builder.ToString();
    }

    private static void AppendPlaceholder(StringBuilder builder, int householdId, DateOnly windowStart, DateTimeOffset nowUtc)
    {
        Append(builder, "BEGIN:VEVENT");
        Append(builder, $"UID:mealplan-{householdId}-empty@family-coordination-app");
        Append(builder, $"DTSTAMP:{nowUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
        Append(builder, $"DTSTART;VALUE=DATE:{windowStart:yyyyMMdd}");
        Append(builder, $"DTEND;VALUE=DATE:{windowStart.AddDays(1):yyyyMMdd}");
        Append(builder, "SUMMARY:No meals planned");
        Append(builder, "END:VEVENT");
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal);

    private static void Append(StringBuilder builder, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var offset = 0;
        var limit = 75;

        while (offset < bytes.Length)
        {
            var count = Math.Min(limit, bytes.Length - offset);
            while (count > 0 && offset + count < bytes.Length && (bytes[offset + count] & 0xC0) == 0x80)
            {
                count--;
            }

            builder.Append(Encoding.UTF8.GetString(bytes, offset, count));
            builder.Append(CrLf);
            offset += count;
            if (offset < bytes.Length)
            {
                builder.Append(' ');
                limit = 74;
            }
        }
    }
}
