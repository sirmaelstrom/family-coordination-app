namespace FamilyCoordinationApp.Services;

/// <summary>
/// Joins parts into a string that fits a column (quest b942b943). A shopping item's breakdown columns are joins
/// whose part count grows with the meal plan, so an unbounded join overflowed its <c>varchar</c> and the save
/// threw. Here the parts that fit are kept whole, in order, and the rest are replaced by a visible
/// <c>… (+N more)</c> marker. The result is never longer than <c>maxLength</c>.
/// </summary>
public static class BoundedJoin
{
    /// <summary>The marker standing in for <paramref name="omitted"/> parts that did not fit.</summary>
    public static string MoreMarker(int omitted) => $"… (+{omitted} more)";

    /// <summary>
    /// <paramref name="parts"/> joined by <paramref name="separator"/>, at most <paramref name="maxLength"/>
    /// characters. With <paramref name="marker"/> false the parts that do not fit are dropped silently (for a
    /// machine-read list, where a marker would not parse).
    /// </summary>
    public static string Join(IReadOnlyList<string> parts, string separator, int maxLength, bool marker = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        // Length of the first k parts joined: prefix[k].
        var prefix = new int[parts.Count + 1];
        for (var i = 0; i < parts.Count; i++)
        {
            prefix[i + 1] = prefix[i] + (i > 0 ? separator.Length : 0) + parts[i].Length;
        }

        // The most whole parts that fit together with the marker for the rest.
        for (var kept = parts.Count; kept >= 0; kept--)
        {
            var omitted = parts.Count - kept;
            var tail = omitted == 0 || !marker
                ? string.Empty
                : (kept > 0 ? separator : string.Empty) + MoreMarker(omitted);

            if (prefix[kept] + tail.Length <= maxLength)
            {
                return string.Join(separator, parts.Take(kept)) + tail;
            }
        }

        // Only when even the bare marker is longer than the column.
        var bare = MoreMarker(parts.Count);
        return bare.Length <= maxLength ? bare : bare[..maxLength];
    }

    /// <summary>
    /// Collapses repeats, keeping first-appearance order: three "1 cup" parts become one "3 × 1 cup" part. Nothing
    /// is lost (the count is kept), and a staple planned in every slot of the week stops costing one part per slot.
    /// </summary>
    public static List<string> CollapseRepeats(IEnumerable<string> parts) =>
        parts
            .GroupBy(p => p, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Count()} × {g.Key}" : g.Key)
            .ToList();
}
