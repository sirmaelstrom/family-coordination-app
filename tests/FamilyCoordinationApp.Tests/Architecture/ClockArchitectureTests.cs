using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// One household clock (quest 5197d71c): app code reads time through <c>IHouseholdClock</c>, never
/// <c>DateTime.UtcNow</c> / <c>DateTime.Now</c> / <c>DateTime.Today</c> / <c>DateTimeOffset.UtcNow</c> /
/// <c>DateTimeOffset.Now</c> directly. The scan reads the comment- and string-stripped <c>src</c>
/// (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>, the same files the tenancy facts scan), so a doc
/// mention is not a read.
/// <para><b>Key:</b> each read is identified by its file and its trimmed source line, compared as a multiset against
/// <see cref="Allowlist"/>. A new read on any other line fails (unlisted); a second copy of an allowed line in the same
/// file fails (the count goes up); a removed or edited allowed line fails (stale), so the list cannot outlive its reason.
/// <b>Stated limits:</b> a read moved to another line in the same file with identical text keeps its key; a read inside
/// an interpolation hole (<c>$"{DateTime.UtcNow}"</c>) is blanked with its string and is invisible (none exist:
/// <c>grep -rnE '\{[^}]*DateTime(Offset)?\.(UtcNow|Now|Today)' src</c> returns nothing).</para>
/// </summary>
public sealed class ClockArchitectureTests
{
    private static readonly Regex DirectRead = new(@"\bDateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now|Today)\b", RegexOptions.Compiled);

    /// <summary>
    /// Direct reads not migrated in this PR, each with its reason. <c>SeedData</c> is a static class (the chore
    /// library is seeded at household creation; the dev seed at Development startup) with 22 test call sites, and
    /// its times are relative offsets for seeded history, not a household's today. Follow-up: take a clock parameter.
    /// </summary>
    internal static readonly (string File, string Line)[] Allowlist =
    [
        ("Data/SeedData.cs", "CreatedAt = DateTime.UtcNow.AddDays(-recipeId)"),
        ("Data/SeedData.cs", "var now = DateTime.UtcNow;"),
        ("Data/SeedData.cs", "var now = DateTime.UtcNow;"),
    ];

    [Fact]
    public void No_direct_time_read_outside_the_allowlist()
    {
        var reads = TenantScopeArchitectureTests.AppSources()
            .SelectMany(f => DirectReads(f.RelativePath, f.Source))
            .ToList();

        var (unlisted, stale) = Compare(reads, Allowlist);

        using (new AssertionScope())
        {
            reads.Should().NotBeEmpty("the scanner must see the allowlisted SeedData reads (guard the guard)");
            unlisted.Should().BeEmpty(
                "app code reads time through IHouseholdClock (quest 5197d71c); inject it, or add the line to the " +
                "allowlist with a reason. Unlisted:\n{0}", string.Join("\n", unlisted));
            stale.Should().BeEmpty(
                "every allowlist entry must name a read that still exists; remove it. Stale:\n{0}", string.Join("\n", stale));
        }
    }

    // ── Negative controls: feed the detector known input ───────────────────────────────

    [Fact]
    public void NC_a_read_in_code_is_found_and_a_mention_in_a_comment_or_string_is_not()
    {
        const string source = """
            /// <see cref="DateTime.UtcNow"/> in a doc comment
            // DateTime.Today in a line comment
            var label = "DateTime.Now";
            var today = DateOnly.FromDateTime(DateTime.Today);
            var stamp = DateTimeOffset . UtcNow;
            """;

        DirectReads("X.cs", source).Select(r => r.Line).Should().Equal(
            "var today = DateOnly.FromDateTime(DateTime.Today);",
            "var stamp = DateTimeOffset . UtcNow;");
    }

    [Fact]
    public void NC_a_new_read_is_unlisted_a_duplicate_of_an_allowed_line_is_unlisted_and_a_gone_entry_is_stale()
    {
        (string, string)[] allow = [("A.cs", "var now = DateTime.UtcNow;")];

        var duplicated = DirectReads("A.cs", "var now = DateTime.UtcNow;\nvar now = DateTime.UtcNow;\n").ToList();
        Compare(duplicated, allow).Unlisted.Should().Equal(["A.cs: var now = DateTime.UtcNow;"]);

        var elsewhere = DirectReads("B.cs", "var now = DateTime.UtcNow;\n").ToList();
        var (unlisted, stale) = Compare(elsewhere, allow);
        unlisted.Should().Equal(["B.cs: var now = DateTime.UtcNow;"]);
        stale.Should().Equal(["A.cs: var now = DateTime.UtcNow;"]);
    }

    // ── Detector ───────────────────────────────────────────────────────────────────────

    internal static IEnumerable<(string File, string Line)> DirectReads(string relativePath, string source)
    {
        var stripped = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        var originalLines = source.Split('\n');
        var strippedLines = stripped.Split('\n');
        for (var i = 0; i < strippedLines.Length; i++)
        {
            foreach (Match _ in DirectRead.Matches(strippedLines[i]))
                yield return (relativePath, originalLines[i].Trim());
        }
    }

    internal static (List<string> Unlisted, List<string> Stale) Compare(
        IReadOnlyList<(string File, string Line)> reads, IReadOnlyList<(string File, string Line)> allow)
    {
        static string Key((string File, string Line) r) => $"{r.File}: {r.Line}";
        var remaining = allow.Select(Key).ToList();
        var unlisted = new List<string>();
        foreach (var key in reads.Select(Key))
        {
            if (!remaining.Remove(key)) unlisted.Add(key);
        }

        return (unlisted, remaining);
    }
}
