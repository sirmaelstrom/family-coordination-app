using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// One key allocator (quest 0328001a, operator answer a): a per-household id is allocated only by
/// <c>IKeyAllocator</c> (<c>Services/KeyAllocator.cs</c>), which wraps <c>max + 1</c> and the unique-violation retry.
/// The scan reads the comment- and string-stripped <c>src</c>
/// (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>), Migrations excluded.
/// <list type="bullet">
/// <item><b>Rule 1:</b> no statement outside the allocator calls <c>.Max(</c> / <c>.MaxAsync(</c> / <c>.MaxBy(</c>
/// (with or without explicit type arguments) and names a key
/// column (<c>.RecipeId</c>, <c>.ItemId</c>, …). The key names come from the real model: every int primary-key property
/// other than <c>HouseholdId</c>. <c>Math.Max</c> is not a query and is skipped.</item>
/// <item><b>Rule 2:</b> nothing outside the allocator and the helper's own file names <c>IdGenerationHelper</c>.</item>
/// </list>
/// <para><b>Stated limits:</b> a statement is the text between the previous <c>;</c>, <c>{</c> or <c>}</c> and the next
/// <c>;</c>, so a <c>Max</c> over keys collected in an earlier statement (<c>var ids = …Select(x =&gt; x.ItemId);</c>
/// then <c>ids.Max()</c>) is not seen, nor is an id counter that never calls <c>Max</c> (<c>OrderByDescending</c> +
/// <c>First</c>, or the fresh-household seed's <c>++choreId</c>). Each would have to be written past this test on
/// purpose.</para>
/// </summary>
public sealed class KeyAllocationArchitectureTests
{
    internal const string AllocatorFile = "Services/KeyAllocator.cs";
    internal const string HelperFile = "Services/IdGenerationHelper.cs";

    // .Max( / .MaxAsync( / .MaxBy( / .MaxByAsync(, each optionally with explicit type arguments (.Max<int?>().
    private static readonly Regex MaxCall = new(
        @"(?<!\bMath\s*)\.\s*Max(?:By)?(?:Async)?\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex HelperReference = new(@"\bIdGenerationHelper\b", RegexOptions.Compiled);

    [Fact]
    public void Rule1_no_max_over_a_key_column_outside_the_allocator()
    {
        var keyNames = KeyNames();
        var sites = TenantScopeArchitectureTests.AppSources()
            .Where(f => f.RelativePath != AllocatorFile)
            .SelectMany(f => KeyMaxSites(f.RelativePath, f.Source, keyNames))
            .ToList();

        using (new AssertionScope())
        {
            keyNames.Should().Contain(["RecipeId", "ItemId", "EntryId", "CompletionId"],
                "the key names come from the real model (guard the guard)");
            sites.Should().BeEmpty(
                "a per-household id is allocated only by IKeyAllocator.NextKeyAsync inside InsertAsync (quest " +
                "0328001a); a second max+1 site is a second allocator with no retry of its own. Sites:\n{0}",
                string.Join("\n", sites));
        }
    }

    [Fact]
    public void Rule2_IdGenerationHelper_is_called_only_by_the_allocator()
    {
        var files = TenantScopeArchitectureTests.AppSources().ToList();
        var references = files
            .Where(f => f.RelativePath != AllocatorFile && f.RelativePath != HelperFile)
            .SelectMany(f => TenantScopeArchitectureTests.ReferenceLines(f.Source, HelperReference)
                .Select(line => $"{f.RelativePath}:{line}"))
            .ToList();

        using (new AssertionScope())
        {
            files.Where(f => f.RelativePath == AllocatorFile)
                .SelectMany(f => TenantScopeArchitectureTests.ReferenceLines(f.Source, HelperReference))
                .Should().NotBeEmpty("the allocator is the helper's one caller (guard the guard: the scan sees it)");
            references.Should().BeEmpty(
                "the unique-violation retry is reached through IKeyAllocator.InsertAsync only (quest 0328001a). " +
                "References:\n{0}", string.Join("\n", references));
        }
    }

    // ── Negative controls: feed the detectors known input ─────────────────────────────

    [Fact]
    public void NC_rule1_finds_key_max_in_every_shape_and_skips_sort_orders_math_max_and_comments()
    {
        const string source = """
            var maxId = await context.Recipes
                .MaxAsync(r => (int?)r.RecipeId, ct) ?? 0;
            var next = list.Items.Count > 0 ? list.Items.Max(i => i.ItemId) : 0;
            var c = await context.ChoreCompletions.Select(cc => (int?)cc.CompletionId).MaxAsync() ?? 0;
            var order = await context.Rooms.MaxAsync(r => (int?)r.SortOrder, ct) ?? 0;
            var hi = Math.Max(invite.HouseholdId, other);
            // context.Recipes.MaxAsync(r => r.RecipeId) in a comment
            var label = "x.Max(r => r.RecipeId)";
            var top = list.Items.MaxBy(i => i.ItemId);
            var g = await context.Recipes.Select(r => (int?)r.RecipeId).MaxAsync<int?>(ct);
            var t = list.Items.Max<ShoppingListItem, int>(i => i.ItemId);
            """;

        KeyMaxSites("X.cs", source, ["RecipeId", "ItemId", "CompletionId"]).Should().Equal(
            "X.cs:1: RecipeId",
            "X.cs:3: ItemId",
            "X.cs:4: CompletionId",
            "X.cs:9: ItemId",
            "X.cs:10: RecipeId",
            "X.cs:11: ItemId");
    }

    [Fact]
    public void NC_rule2_finds_a_call_and_skips_a_mention_in_a_comment_or_string()
    {
        const string source = """
            /// <see cref="IdGenerationHelper"/>
            var s = "IdGenerationHelper";
            return await IdGenerationHelper.ExecuteWithRetryAsync(op, logger, "Room");
            """;

        TenantScopeArchitectureTests.ReferenceLines(source, HelperReference).Should().Equal([3]);
    }

    // ── Detector ───────────────────────────────────────────────────────────────────────

    /// <summary>The int primary-key property names of every entity in the real model, <c>HouseholdId</c> excluded.</summary>
    internal static HashSet<string> KeyNames()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"keys-{Guid.NewGuid():N}").Options);
        return db.Model.GetEntityTypes()
            .SelectMany(t => t.FindPrimaryKey()?.Properties ?? [])
            .Where(p => p.ClrType == typeof(int) && p.Name != "HouseholdId")
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Each <c>Max</c> call whose statement names a key, as <c>file:line: KeyName</c> (the line the statement starts
    /// on). One entry per statement, naming the first key it mentions in sorted order.
    /// </summary>
    internal static IEnumerable<string> KeyMaxSites(string relativePath, string source, IReadOnlyCollection<string> keyNames)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        var keyPattern = new Regex(
            @"\.\s*(" + string.Join("|", keyNames.OrderBy(k => k, StringComparer.Ordinal).Select(Regex.Escape)) + @")\b");
        var seen = new HashSet<int>();
        foreach (Match match in MaxCall.Matches(code))
        {
            var start = code.LastIndexOfAny([';', '{', '}'], match.Index) + 1;
            var end = code.IndexOf(';', match.Index);
            if (end < 0) end = code.Length;
            if (!seen.Add(start)) continue;

            var key = keyPattern.Matches(code[start..end]).Select(m => m.Groups[1].Value)
                .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (key is null) continue;

            var firstCodeChar = start;
            while (firstCodeChar < end && char.IsWhiteSpace(code[firstCodeChar])) firstCodeChar++;
            yield return $"{relativePath}:{code[..firstCodeChar].Count(c => c == '\n') + 1}: {key}";
        }
    }
}
