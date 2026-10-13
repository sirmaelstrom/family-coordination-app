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
                "a per-household id is allocated only by IKeyAllocator (NextKeyAsync, or NextKeyFromSnapshot for " +
                "the pinned read-and-replace) inside InsertAsync (quest " +
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

    /// <summary>
    /// Rule 3: <c>NextKeyFromSnapshot</c> is the in-memory max the allocator keeps for one read-and-replace, so its
    /// callers are pinned. A snapshot loaded through a <c>SoftDelete</c>-filtered query would reuse a deleted id and
    /// collide on every retry; a new caller must show its snapshot is unfiltered and be added here.
    /// </summary>
    internal static readonly Dictionary<string, int> SnapshotCallersPerFile = new(StringComparer.Ordinal)
    {
        ["Services/ShoppingListGenerator.cs"] = 1, // RegenerateOnceAsync: ShoppingListItem has no SoftDelete filter
    };

    [Fact]
    public void Rule3_NextKeyFromSnapshot_has_only_its_pinned_callers()
    {
        var calls = TenantScopeArchitectureTests.AppSources()
            .Where(f => f.RelativePath != AllocatorFile)
            .Select(f => (f.RelativePath, Count: TenantScopeArchitectureTests.ReferenceLines(f.Source, SnapshotCall).Count))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.RelativePath, f => f.Count, StringComparer.Ordinal);

        calls.Should().BeEquivalentTo(SnapshotCallersPerFile,
            "the snapshot allocator is for a read-and-replace over an unfiltered snapshot only (council round 1 on " +
            "PR #145). In src: " + string.Join(", ", calls.Select(c => $"{c.Key}={c.Value}")));
    }

    private static readonly Regex SnapshotCall = new(@"\.\s*NextKeyFromSnapshot\s*\(", RegexOptions.Compiled);

    /// <summary>The DbSet each <c>NextKeyAsync(context.X…)</c> call allocates in.</summary>
    private static readonly Regex NextKeyScope = new(@"\.\s*NextKeyAsync\s*\(\s*context\s*\.\s*(\w+)", RegexOptions.Compiled);

    /// <summary>The table each pinned <c>NextKeyFromSnapshot</c> caller allocates in (Rule 3 pins the callers).</summary>
    private static readonly Dictionary<string, Type> SnapshotCallerEntity = new(StringComparer.Ordinal)
    {
        ["Services/ShoppingListGenerator.cs"] = typeof(FamilyCoordinationApp.Data.Entities.ShoppingListItem),
    };

    [Fact]
    public void Rule4_every_allocated_table_primary_key_is_retryable()
    {
        // Quest d9da6ca4: the allocator retries only RetryableConstraints. A table allocated through IKeyAllocator
        // whose primary key is missing there would answer its id races with 409 instead of retrying.
        var sources = TenantScopeArchitectureTests.AppSources().Where(f => f.RelativePath != AllocatorFile).ToList();
        var dbSets = sources
            .SelectMany(f => NextKeyScope.Matches(TenantScopeArchitectureTests.StripCommentsAndStrings(f.Source)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var entities = dbSets
            .Select(name => typeof(ApplicationDbContext).GetProperty(name)?.PropertyType.GetGenericArguments().SingleOrDefault()
                ?? throw new InvalidOperationException($"NextKeyAsync(context.{name}…) does not name a DbSet on ApplicationDbContext"))
            .Concat(SnapshotCallerEntity.Values)
            .ToHashSet();

        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        var keyNames = entities.ToDictionary(e => e.Name, e => db.Model.FindEntityType(e)!.FindPrimaryKey()!.GetName());

        using (new AssertionScope())
        {
            dbSets.Should().Contain(["Recipes", "Categories", "Chores", "Rooms", "MealPlans", "MealPlanEntries", "ShoppingLists", "ShoppingListItems"],
                "the scan sees the allocator's call sites (guard the guard)");
            SnapshotCallerEntity.Keys.Should().BeEquivalentTo(SnapshotCallersPerFile.Keys, "a new snapshot caller names its table here");
            keyNames.Values.Should().BeSubsetOf(FamilyCoordinationApp.Services.IdGenerationHelper.RetryableConstraints,
                "every allocated table's primary key is a collision a retry cures. Keys: {0}",
                string.Join(", ", keyNames.Select(k => $"{k.Key}={k.Value}")));
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

    [Fact]
    public void NC_rule3_finds_a_snapshot_call_and_skips_a_mention_in_a_comment_or_string()
    {
        const string source = """
            // keys.NextKeyFromSnapshot(ids) in a comment
            var s = "keys.NextKeyFromSnapshot(ids)";
            var next = keys
                .NextKeyFromSnapshot(list.Items.Select(i => i.ItemId));
            """;

        // The match starts at the `.` of the split call, on line 4.
        TenantScopeArchitectureTests.ReferenceLines(source, SnapshotCall).Should().Equal([4]);
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
