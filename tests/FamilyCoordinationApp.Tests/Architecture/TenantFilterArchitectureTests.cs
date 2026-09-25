using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// The read switch's guard facts (fca-household-scope WP-02 step 6; decisions D7). Each scans the comment- and
/// string-stripped <c>src</c> (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>), skips the method
/// definitions in <c>Tenancy/*.cs</c>, and ships an in-tree negative control that feeds its detector known-bad input.
/// <list type="bullet">
/// <item><b>Fact 1:</b> no bare <c>IgnoreQueryFilters()</c>: it would drop <c>"Tenant"</c> along with soft delete.</item>
/// <item><b>Fact 2:</b> every <c>IgnoreQueryFilters(…)</c> whose argument is not exactly <c>["SoftDelete"]</c> is a
/// Tenant bypass. It needs a <c>// TENANT-SCOPE-OK:</c> pragma in the comment run directly above the FIRST line of its
/// statement, and the set of bypass statements equals, <c>file:line</c> for <c>file:line</c>, the
/// <c>bypass:</c> rows of <c>.planning/tenancy-bypass-inventory.md</c>. Treating any non-soft-delete argument as a
/// bypass (a variable, <c>new[] { … }</c>) is deliberate: a list the scan can't read is not a list it can clear.</item>
/// <item><b>Fact 4:</b> the files calling <c>.RunAs(</c> are exactly the D11 allowlist.</item>
/// <item><b>Fact 5:</b> every <see cref="ITenantEntity"/> has a query filter named <c>"Tenant"</c>, and the
/// <see cref="ITenantEntity"/> types are exactly the entities with a <c>*HouseholdId*</c> property minus
/// {<see cref="Feedback"/>, <see cref="HouseholdConnection"/>}.</item>
/// </list>
/// Facts 3, 6 and 7, and the <c>CreateUnfiltered</c>/<c>SetCaller</c> allowlists, shipped in WP-01.
/// <para><b>Stated limit (fact 2):</b> the statement start is found by walking back to the previous <c>;</c>,
/// <c>{</c> or <c>}</c>, so a block-bodied lambda earlier in the same statement moves the start to inside it. The
/// pragma would then have to sit inside the lambda, which fails safe (a flag, not a pass).</para>
/// </summary>
public class TenantFilterArchitectureTests
{
    internal static readonly string[] RunAsAllowedFiles =
    [
        "Services/Digest/DigestService.cs",
        "Endpoints/CalendarTokenEndpoints.cs",
        "Services/SetupService.cs",
        "Services/HouseholdRequestService.cs",
        "Data/SeedData.cs",
        "Services/LoginProfileService.cs",
    ];

    /// <summary>Tenant entities that deliberately have no Tenant filter (D5): a nullable dual-mode id and a pair.</summary>
    internal static readonly Type[] UnfilteredHouseholdIdEntities = [typeof(Feedback), typeof(HouseholdConnection)];

    private static readonly Regex IgnoreQueryFiltersCallRe = new(@"\bIgnoreQueryFilters\s*\(");
    private static readonly Regex SoftDeleteOnlyArgumentRe = new(@"^\s*\[\s*""SoftDelete""\s*\]\s*$");
    private static readonly Regex RunAsCallRe = new(@"\.\s*RunAs\s*\(");

    // ── The detectors (pure; the negative controls exercise them) ────────────

    /// <summary>One <c>IgnoreQueryFilters</c> call: its argument text (from the ORIGINAL source) and statement start.</summary>
    internal sealed record FilterCall(int Line, int StatementLine, string Argument, bool PragmaAboveStatement)
    {
        public bool IsBare => Argument.Trim().Length == 0;
        public bool IsTenantBypass => !IsBare && !SoftDeleteOnlyArgumentRe.IsMatch(Argument);
    }

    internal static List<FilterCall> FilterCalls(string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        var calls = new List<FilterCall>();
        foreach (Match m in IgnoreQueryFiltersCallRe.Matches(code))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingParen(code, open);
            // Stripping keeps offsets, so the argument's string literals are read back from the original source.
            var argument = source[(open + 1)..close];
            var start = StatementStart(code, m.Index);
            calls.Add(new FilterCall(
                LineOf(code, m.Index),
                LineOf(code, start),
                argument,
                TenantScopeArchitectureTests.PragmaCovers(source, start)));
        }
        return calls;
    }

    /// <summary>The index of the first non-whitespace character after the previous <c>;</c>, <c>{</c> or <c>}</c>.</summary>
    internal static int StatementStart(string code, int index)
    {
        var i = index - 1;
        while (i >= 0 && code[i] is not (';' or '{' or '}')) i--;
        i++;
        while (i < index && char.IsWhiteSpace(code[i])) i++;
        return i;
    }

    private static int MatchingParen(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')' && --depth == 0) return i;
        }
        throw new InvalidOperationException($"unbalanced parenthesis at offset {open}");
    }

    private static int LineOf(string code, int index) => code[..index].Count(c => c == '\n') + 1;

    internal static List<int> RunAsCallLines(string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        return RunAsCallRe.Matches(code).Select(m => LineOf(code, m.Index)).Distinct().ToList();
    }

    /// <summary>Fact 5 over any model: the reasons it fails, empty when it holds.</summary>
    internal static List<string> FilterCoverageViolations(IModel model, IReadOnlyCollection<Type> unfilteredHouseholdIdEntities)
    {
        var violations = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var isTenant = typeof(ITenantEntity).IsAssignableFrom(clr);
            var hasHouseholdId = clr.GetProperties().Any(p => p.Name.Contains("HouseholdId"));
            var hasTenantFilter = entityType.GetDeclaredQueryFilters().Any(f => f.Key == "Tenant");

            if (isTenant && !hasTenantFilter)
                violations.Add($"{clr.Name} implements ITenantEntity but has no query filter named \"Tenant\"");
            if (hasHouseholdId && !isTenant && !unfilteredHouseholdIdEntities.Contains(clr))
                violations.Add($"{clr.Name} has a HouseholdId property but does not implement ITenantEntity");
            if (isTenant && unfilteredHouseholdIdEntities.Contains(clr))
                violations.Add($"{clr.Name} is on the reviewed no-filter list but implements ITenantEntity");
        }
        return violations;
    }

    // ── The real tree ───────────────────────────────────────────────────────

    private static IEnumerable<(string RelativePath, string Source)> AppSources()
    {
        var root = TenantScopeArchitectureTests.AppSourceRoot();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace('\\', '/'), Full: f))
            .Where(f => !f.Path.StartsWith("Migrations/", StringComparison.Ordinal)
                        && !f.Path.StartsWith("bin/", StringComparison.Ordinal)
                        && !f.Path.StartsWith("obj/", StringComparison.Ordinal)
                        && !f.Path.StartsWith("Tenancy/", StringComparison.Ordinal)) // the definitions match their own patterns
            .Select(f => (f.Path, File.ReadAllText(f.Full)))
            .ToList();
    }

    private static List<(string File, FilterCall Call)> AllFilterCalls() =>
        AppSources().SelectMany(f => FilterCalls(f.Source).Select(c => (f.RelativePath, c))).ToList();

    /// <summary>The inventory's <c>bypass:</c> rows as <c>file:line</c> (the line is the statement's first line).</summary>
    internal static List<string> InventoryBypassRows(string markdown) =>
        Regex.Matches(markdown, @"^\|\s*`(?<loc>[^`]+:\d+)`\s*\|.*?\|\s*bypass:", RegexOptions.Multiline)
            .Select(m => m.Groups["loc"].Value)
            .ToList();

    private static string InventoryText()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(TenantScopeArchitectureTests.AppSourceRoot(), "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, ".planning", "tenancy-bypass-inventory.md"));
    }

    [Fact]
    public void Fact1_no_bare_IgnoreQueryFilters_in_src()
    {
        var offenders = AllFilterCalls().Where(c => c.Call.IsBare).Select(c => $"{c.File}:{c.Call.Line}").ToList();

        offenders.Should().BeEmpty(
            "a bare IgnoreQueryFilters() drops the \"Tenant\" filter along with soft delete (D5, A2); name the filter, " +
            "IgnoreQueryFilters([\"SoftDelete\"]). Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Fact2_every_Tenant_bypass_carries_a_pragma_above_its_statement()
    {
        var offenders = AllFilterCalls()
            .Where(c => c.Call.IsTenantBypass && !c.Call.PragmaAboveStatement)
            .Select(c => $"{c.File}:{c.Call.StatementLine} (call at :{c.Call.Line})")
            .ToList();

        offenders.Should().BeEmpty(
            "a Tenant bypass needs a `// TENANT-SCOPE-OK: <reason naming the gate>` pragma in the comment run directly " +
            "above the first line of its statement (MN3, P2). Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Fact2_the_Tenant_bypasses_in_src_are_exactly_the_inventory_bypass_rows()
    {
        var inSource = AllFilterCalls().Where(c => c.Call.IsTenantBypass)
            .Select(c => $"{c.File}:{c.Call.StatementLine}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        var inInventory = InventoryBypassRows(InventoryText()).OrderBy(s => s, StringComparer.Ordinal).ToList();

        inSource.Should().NotBeEmpty("the scan must see the bypasses the switch placed (guard the guard)");
        inSource.Should().Equal(inInventory,
            "every Tenant bypass in src has an inventory row and every `bypass:` row names a bypass in src (M4, V6); " +
            "a row's file:line is its statement's first line. In src only: " +
            string.Join(", ", inSource.Except(inInventory)) + ". In the inventory only: " +
            string.Join(", ", inInventory.Except(inSource)));
    }

    [Fact]
    public void Fact4_RunAs_is_called_only_from_the_allowlisted_files()
    {
        var callers = AppSources().Where(f => RunAsCallLines(f.Source).Count > 0).Select(f => f.RelativePath)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

        callers.Should().Equal(RunAsAllowedFiles.OrderBy(s => s, StringComparer.Ordinal),
            "RunAs is system work outside a caller: exactly the digest, the calendar feed, setup, admin approve/create, " +
            "the dev seed and the login profile write use it (D11), and each of them does");
    }

    [Fact]
    public void Fact5_every_tenant_entity_has_the_Tenant_filter_and_the_tenant_set_is_the_name_derived_set()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"fact5-{Guid.NewGuid():N}").Options);

        var tenantTypes = db.Model.GetEntityTypes().Count(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType));

        tenantTypes.Should().Be(21, "D5's 21 tenant entities (guard the guard: the model is the real one)");
        FilterCoverageViolations(db.Model, UnfilteredHouseholdIdEntities).Should().BeEmpty();
    }

    // ── In-tree negative controls: each detector fails on known-bad input ────

    [Fact]
    public void NC_fact1_a_bare_call_is_flagged_and_a_commented_one_is_not()
    {
        const string source = """
            var a = await context.Recipes.IgnoreQueryFilters().ToListAsync();
            var b = await context.Recipes.IgnoreQueryFilters( ).ToListAsync();
            // var c = context.Recipes.IgnoreQueryFilters();
            var d = "IgnoreQueryFilters()";
            var e = await context.Recipes.IgnoreQueryFilters(["SoftDelete"]).ToListAsync();
            """;

        FilterCalls(source).Where(c => c.IsBare).Select(c => c.Line).Should().Equal(1, 2);
    }

    [Fact]
    public void NC_fact2_bypass_classification_and_statement_based_pragma_adjacency()
    {
        const string source = """
            class S
            {
                async Task M()
                {
                    // TENANT-SCOPE-OK: gated by X at Y.cs:1
                    var ok = await context.Users
                        .IgnoreQueryFilters(["Tenant"])
                        .ToListAsync();

                    var noPragma = await context.Users.IgnoreQueryFilters(["Tenant", "SoftDelete"]).ToListAsync();

                    var pragmaOnTheChainLine = await context.Users
                        // TENANT-SCOPE-OK: this sits on the chain, not above the statement
                        .IgnoreQueryFilters(["Tenant"])
                        .ToListAsync();

                    var viaVariable = await context.Users.IgnoreQueryFilters(names).ToListAsync();

                    var softDeleteOnly = await context.Recipes.IgnoreQueryFilters([ "SoftDelete" ]).ToListAsync();

                    // TENANT-SCOPE-OK:
                    var barePragma = await context.Users.IgnoreQueryFilters(["Tenant"]).ToListAsync();
                }
            }
            """;

        var calls = FilterCalls(source);

        calls.Where(c => c.IsTenantBypass).Select(c => c.StatementLine).Should().Equal(6, 10, 12, 17, 22);
        calls.Where(c => c.IsTenantBypass && !c.PragmaAboveStatement).Select(c => c.StatementLine)
            .Should().Equal([10, 12, 17, 22], "only the first carries a reasoned pragma above its statement's first line");
        calls.Single(c => c.StatementLine == 19).IsTenantBypass.Should().BeFalse("soft delete alone is not a Tenant bypass");
    }

    [Fact]
    public void NC_fact2_inventory_rows_are_parsed_by_classification()
    {
        const string markdown = """
            | file:line | expression (trimmed) | classification | gate / household source |
            |---|---|---|---|
            | `Services/A.cs:10` | `Users.Where(…)` | bypass: identity resolution | the email claim |
            | `Services/A.cs:20` | `x.HouseholdId == householdId` | caller-scoped (no change) | caller |
            | `Services/B.cs:5` | `RunAs(h)` | RunAs: the swept household | rows |
            | `Services/B.cs:9` | `Recipes.IgnoreQueryFilters(["Tenant"])` | bypass: connected recipes | AreHouseholdsConnectedAsync |
            """;

        InventoryBypassRows(markdown).Should().Equal("Services/A.cs:10", "Services/B.cs:9");
    }

    [Fact]
    public void NC_fact4_a_RunAs_call_is_seen_and_a_mention_is_not()
    {
        const string source = """
            // context.Tenant.RunAs(1) in a comment
            var s = "tenant.RunAs(2)";
            using var scope = context.Tenant
                .RunAs(householdId);
            """;

        RunAsCallLines(source).Should().Equal(4);
        RunAsCallLines("public IDisposable RunAs(int householdId) { }").Should().BeEmpty("a definition is not a call");
    }

    [Fact]
    public void NC_fact5_a_HouseholdId_entity_without_the_marker_and_a_marked_entity_without_the_filter_are_flagged()
    {
        using var db = new CoverageNcContext();

        FilterCoverageViolations(db.Model, [typeof(CoverageNcPair)]).Should().BeEquivalentTo(
            "CoverageNcOrphan has a HouseholdId property but does not implement ITenantEntity",
            "CoverageNcStray implements ITenantEntity but has no query filter named \"Tenant\"");
    }

    public sealed class CoverageNcOrphan
    {
        public int Id { get; set; }
        public int HouseholdId { get; set; }
    }

    public sealed class CoverageNcStray : ITenantEntity
    {
        public int Id { get; set; }
        public int HouseholdId { get; set; }
    }

    public sealed class CoverageNcFiltered : ITenantEntity
    {
        public int Id { get; set; }
        public int HouseholdId { get; set; }
    }

    public sealed class CoverageNcPair
    {
        public int Id { get; set; }
        public int HouseholdId1 { get; set; }
    }

    /// <summary>A test-only model: one entity per fact-5 case. Only <see cref="CoverageNcFiltered"/> gets the filter.</summary>
    private sealed class CoverageNcContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseInMemoryDatabase($"fact5-nc-{Guid.NewGuid():N}");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CoverageNcOrphan>();
            modelBuilder.Entity<CoverageNcStray>();
            modelBuilder.Entity<CoverageNcPair>();
            modelBuilder.Entity<CoverageNcFiltered>().HasQueryFilter("Tenant", e => e.HouseholdId == 1);
        }
    }
}
