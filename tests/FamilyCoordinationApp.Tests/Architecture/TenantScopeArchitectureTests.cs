using System.Reflection;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// The tenant-boundary architecture guard (quest ec788d69, WP-1 — guard first;
/// the HouseholdScope refactor itself waits for its spec sitting).
///
/// THE INVARIANT (CLAUDE.md, "Multi-tenant isolation"): every query against a
/// tenant entity — an entity carrying a HouseholdId — filters by HouseholdId.
/// It is a security boundary, not a convention. This test makes the convention
/// mechanical: it scans every app source file for DbSet accesses on tenant
/// entities and fails when a QUERY statement neither mentions HouseholdId nor
/// carries an explicit <c>// TENANT-SCOPE-OK: reason</c> pragma.
///
/// What counts, and what deliberately does not:
///  - The tenant-entity list is DERIVED from ApplicationDbContext by reflection
///    (every DbSet whose entity type has a HouseholdId property) — a new tenant
///    DbSet is guarded from birth, with no list to forget to update.
///  - Only QUERY verbs are checked. A mutation verb called directly on the set
///    (Add/Remove/Update/Attach…) operates on an entity constructed with its
///    HouseholdId or loaded by an already-scoped query — flagging those would
///    bury the signal (measured at baseline: 53 raw flags → 14 real reads).
///  - The scope window is ONE STATEMENT (to the terminating semicolon).
///    A builder-pattern query that applies scope in a later statement takes the
///    pragma, naming where the scope lands (see FeedbackService).
///  - The pragma REQUIRES a reason. A bare pragma fails the scan.
///
/// Baseline adjudicated 2026-08-29: 13 pragmas (auth/identity resolution before
/// a household exists, dev-only paths, invite redemption which is cross-household
/// by design, dual-mode site-admin queries scoped conditionally) and one fix
/// (DashboardService.GetHouseholdNameAsync gained the household predicate).
/// </summary>
public class TenantScopeArchitectureTests
{
    // ── The scanner (pure; exercised by the negative controls below) ─────────

    internal static readonly string[] MutationVerbs =
    [
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Remove", "RemoveRange", "Update", "UpdateRange",
        "Attach", "AttachRange", "Entry", "Local",
    ];

    internal sealed record Violation(string File, int Line, string Snippet);

    internal static List<Violation> Scan(string source, string fileLabel, IReadOnlyCollection<string> tenantSets)
    {
        var violations = new List<Violation>();

        // Context receivers bound in this file: factory-created contexts plus
        // directly-typed parameters/fields (the house rule is the factory, but
        // the guard should not depend on the house rule being followed).
        var receivers = new HashSet<string>();
        foreach (Match m in Regex.Matches(source, @"(?:var|using\s+var)\s+(\w+)\s*=\s*await\s+\w+\.CreateDbContextAsync"))
            receivers.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(source, @"ApplicationDbContext\s+(\w+)\s*[,)=;{]"))
            receivers.Add(m.Groups[1].Value);
        if (receivers.Count == 0) return violations;

        var recvAlt = string.Join("|", receivers.Select(Regex.Escape));
        var setAlt = string.Join("|", tenantSets.Select(Regex.Escape));
        var occurrence = new Regex($@"\b(?:{recvAlt})\s*\.\s*(?:{setAlt})\b");

        foreach (Match m in occurrence.Matches(source))
        {
            // First method invoked directly on the DbSet decides the class.
            var afterSet = source[(m.Index + m.Length)..];
            var firstMethod = Regex.Match(afterSet, @"^\s*\.\s*(\w+)");
            if (firstMethod.Success && MutationVerbs.Contains(firstMethod.Groups[1].Value))
                continue;

            // Statement window: to the terminating semicolon.
            var semi = source.IndexOf(';', m.Index);
            var window = semi >= 0 ? source[m.Index..(semi + 1)] : source[m.Index..];

            // Pragma: on the statement itself or within the preceding lines.
            var before = source[Math.Max(0, m.Index - 240)..m.Index];
            // The reason must sit on the pragma's own line — [^\S\n] is whitespace
            // except newline, so a bare pragma cannot borrow the next statement
            // as its "reason".
            var pragma = Regex.IsMatch(before + window, @"TENANT-SCOPE-OK:[^\S\n]*\S");

            if (!window.Contains("HouseholdId") && !pragma)
            {
                var line = source[..m.Index].Count(c => c == '\n') + 1;
                var snippet = Regex.Replace(window, @"\s+", " ");
                violations.Add(new Violation(fileLabel, line, snippet[..Math.Min(140, snippet.Length)]));
            }
        }
        return violations;
    }

    internal static IReadOnlyCollection<string> TenantDbSets()
    {
        return typeof(ApplicationDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)
                        && p.PropertyType.GetGenericArguments()[0].GetProperty("HouseholdId") is not null)
            .Select(p => p.Name)
            .ToList();
    }

    internal static string AppSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FamilyCoordinationApp")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from within the repository tree");
        return Path.Combine(dir!.FullName, "src", "FamilyCoordinationApp");
    }

    private static IEnumerable<string> AppSourceFiles()
    {
        var root = AppSourceRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    // ── The invariant ────────────────────────────────────────────────────────

    [Fact]
    public void Every_tenant_entity_query_is_household_scoped_or_carries_a_reasoned_pragma()
    {
        var tenantSets = TenantDbSets();
        var root = AppSourceRoot();
        var violations = new List<Violation>();

        foreach (var file in AppSourceFiles())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(Scan(File.ReadAllText(file), rel, tenantSets));
        }

        violations.Should().BeEmpty(
            "every query against a tenant DbSet must filter by HouseholdId within its statement, " +
            "or carry `// TENANT-SCOPE-OK: <reason>` naming why it is legitimately unscoped. Violations:\n" +
            string.Join("\n", violations.Select(v => $"  {v.File}:{v.Line}  {v.Snippet}")));
    }

    // ── Guard-the-guard: the instrument must actually be measuring ──────────
    // A scanner that derives an empty entity list, finds no receivers, or scans
    // no occurrences would report "clean" while checking nothing (the
    // coverage-counter lesson: count VALID contributions, not just absence of
    // failures).

    [Fact]
    public void The_tenant_entity_derivation_is_alive()
    {
        var sets = TenantDbSets();
        sets.Should().HaveCountGreaterThan(15);
        sets.Should().Contain(["Recipes", "Chores", "ShoppingLists", "Users"]);
        // Household itself is NOT a tenant entity — it IS the tenant.
        sets.Should().NotContain("Households");
    }

    [Fact]
    public void The_scan_visits_a_substantial_occurrence_population()
    {
        var tenantSets = TenantDbSets();
        var total = 0;
        foreach (var file in AppSourceFiles())
        {
            var source = File.ReadAllText(file);
            var receivers = new HashSet<string>();
            foreach (Match m in Regex.Matches(source, @"(?:var|using\s+var)\s+(\w+)\s*=\s*await\s+\w+\.CreateDbContextAsync"))
                receivers.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(source, @"ApplicationDbContext\s+(\w+)\s*[,)=;{]"))
                receivers.Add(m.Groups[1].Value);
            if (receivers.Count == 0) continue;
            var re = new Regex($@"\b(?:{string.Join("|", receivers)})\s*\.\s*(?:{string.Join("|", tenantSets)})\b");
            total += re.Matches(source).Count;
        }
        // 216 measured at adoption (2026-08-29). Shrinking dramatically below
        // that means the scan stopped seeing the codebase, not that the
        // codebase stopped querying.
        total.Should().BeGreaterThan(150);
    }

    // ── Negative controls: the scanner fails on known-bad input ─────────────

    private static readonly string[] NcSets = ["Recipes"];

    [Fact]
    public void NC_an_unscoped_query_is_flagged()
    {
        const string bad = """
            var context = await factory.CreateDbContextAsync();
            var all = await context.Recipes.ToListAsync();
            """;
        Scan(bad, "nc.cs", NcSets).Should().ContainSingle();
    }

    [Fact]
    public void NC_a_scoped_query_passes()
    {
        const string good = """
            var context = await factory.CreateDbContextAsync();
            var mine = await context.Recipes.Where(r => r.HouseholdId == scope.HouseholdId).ToListAsync();
            """;
        Scan(good, "nc.cs", NcSets).Should().BeEmpty();
    }

    [Fact]
    public void NC_a_mutation_on_the_set_passes_without_scope()
    {
        const string mutation = """
            var context = await factory.CreateDbContextAsync();
            context.Recipes.Add(recipe);
            """;
        Scan(mutation, "nc.cs", NcSets).Should().BeEmpty();
    }

    [Fact]
    public void NC_a_reasoned_pragma_passes_and_a_bare_pragma_does_not()
    {
        const string reasoned = """
            var context = await factory.CreateDbContextAsync();
            // TENANT-SCOPE-OK: seed guard, dev-only database
            var any = await context.Recipes.AnyAsync();
            """;
        Scan(reasoned, "nc.cs", NcSets).Should().BeEmpty();

        const string bare = """
            var context = await factory.CreateDbContextAsync();
            // TENANT-SCOPE-OK:
            var any = await context.Recipes.AnyAsync();
            """;
        Scan(bare, "nc.cs", NcSets).Should().ContainSingle("a pragma without a reason is not an adjudication");
    }
}
