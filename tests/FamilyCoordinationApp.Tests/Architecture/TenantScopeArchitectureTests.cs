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
/// entities and fails when a QUERY statement neither carries a HouseholdId
/// COMPARISON nor an explicit <c>// TENANT-SCOPE-OK: reason</c> pragma.
///
/// What counts, and what deliberately does not:
///  - The tenant-entity list is DERIVED from ApplicationDbContext by reflection
///    (every DbSet whose entity type has a property whose NAME CONTAINS
///    "HouseholdId" — HouseholdConnection scopes via HouseholdId1/2) — a new
///    tenant DbSet is guarded from birth, with no list to forget to update,
///    and the unguarded complement is pinned exactly by its own fact below.
///  - Scope means a COMPARISON on a HouseholdId-family column (==/!=/Contains),
///    never a mention — a projection like `.Select(r => r.HouseholdId)` is a
///    cross-tenant read wearing the right word (council r1).
///  - Only QUERY verbs are checked. A mutation verb called directly on the set
///    (Add/Remove/Update/Attach/Entry) operates on an entity constructed with
///    its HouseholdId or loaded by an already-scoped query — flagging those
///    would bury the signal (measured at baseline: 53 raw flags → real reads).
///  - The scope window is ONE STATEMENT (to the terminating semicolon; a ';'
///    inside a string literal truncates it early, which fails SAFE — a false
///    flag, never a bypass). A builder-pattern query that applies scope in a
///    later statement takes the pragma, naming where the scope lands (see
///    FeedbackService).
///  - The pragma REQUIRES a reason, a real `//` marker, and line adjacency:
///    the occurrence's own line, or the contiguous comment/blank run directly
///    above it. A bare pragma fails; a pragma cannot leak onto the next
///    statement.
///
/// Baseline adjudicated 2026-08-29 (amended at council r1, which surfaced four
/// projection-passing reads the first scan missed): 19 pragmas — auth/identity
/// resolution before a household exists, identity resolution that IS the scope
/// source (UserContextResolver/Me/Presence), dev-only paths, invite redemption
/// which is cross-household by design, dual-mode site-admin queries scoped
/// conditionally, and the digest cron's all-households sweep — and one fix
/// (DashboardService.GetHouseholdNameAsync gained the household predicate).
/// </summary>
public class TenantScopeArchitectureTests
{
    // ── The scanner (pure; exercised by the negative controls below) ─────────

    // "Entry" stays: it takes an already-tracked entity, same class as Attach.
    // "Local" is deliberately NOT here (council r1, opus): it is a READ over the
    // change tracker and must justify itself like any other read.
    internal static readonly string[] MutationVerbs =
    [
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Remove", "RemoveRange", "Update", "UpdateRange",
        "Attach", "AttachRange", "Entry",
    ];

    internal sealed record Violation(string File, int Line, string Snippet);

    /// <summary>
    /// Context receivers bound in a file: factory-created contexts (async OR
    /// sync — the sync `CreateDbContext()` used to blind the scanner for a
    /// whole file, council r1 opus #3 — with a possibly-qualified factory
    /// expression like `this.dbFactory`), plus directly-typed parameters and
    /// fields. Shared by Scan and the coverage counter so the two cannot
    /// diverge (council r1, opus #7).
    /// </summary>
    internal static HashSet<string> Receivers(string source)
    {
        var receivers = new HashSet<string>();
        foreach (Match m in Regex.Matches(source, @"(?:var|using\s+var)\s+(\w+)\s*=\s*(?:await\s+)?[\w.]+\.CreateDbContext(?:Async)?\b"))
            receivers.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(source, @"ApplicationDbContext\s+(\w+)\s*[,)=;{]"))
            receivers.Add(m.Groups[1].Value);
        return receivers;
    }

    /// <summary>
    /// Scope = a COMPARISON on a HouseholdId-family column, not a mention
    /// (council r1, all three lenses): a projection like
    /// `.Select(r => r.HouseholdId)` is a cross-tenant read wearing the right
    /// word. `\w*` admits composite columns (HouseholdId1/HouseholdId2).
    /// </summary>
    internal static bool WindowIsScoped(string window) =>
        Regex.IsMatch(window, @"HouseholdId\w*\s*(==|!=)")
        || Regex.IsMatch(window, @"(==|!=)\s*[\w.?]*[Hh]ouseholdId\w*")
        || Regex.IsMatch(window, @"Contains\([^)]*HouseholdId");

    // Must be a real `//` comment, and the reason must sit on the pragma's own
    // line — [^\S\n] is whitespace except newline, so a bare pragma cannot
    // borrow the next line as its "reason".
    private static readonly Regex PragmaRe = new(@"//\s*TENANT-SCOPE-OK:[^\S\n]*\S");

    internal static bool PragmaCovers(string source, int occurrenceIndex)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, occurrenceIndex - 1)) + 1;
        if (PragmaRe.IsMatch(source[lineStart..occurrenceIndex])) return true; // same line, before the occurrence

        // Walk upward over contiguous comment-only / blank lines.
        var end = lineStart; // exclusive end of the line above (points at its trailing '\n' + 1)
        while (end > 0)
        {
            var prevStart = source.LastIndexOf('\n', Math.Max(0, end - 2)) + 1;
            var prevLine = source[prevStart..(end - 1)].TrimEnd('\r');
            if (!Regex.IsMatch(prevLine, @"^\s*(//|$)")) return false; // a CODE line breaks the run
            if (PragmaRe.IsMatch(prevLine)) return true;
            if (prevStart == 0) return false;
            end = prevStart;
        }
        return false;
    }

    internal static List<Violation> Scan(string source, string fileLabel, IReadOnlyCollection<string> tenantSets)
    {
        var violations = new List<Violation>();
        var receivers = Receivers(source);
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

            // Statement window: to the terminating semicolon. (Lexically naive:
            // a ';' inside a string literal truncates the window early — that
            // fails in the SAFE direction, a false flag, never a bypass.)
            var semi = source.IndexOf(';', m.Index);
            var window = semi >= 0 ? source[m.Index..(semi + 1)] : source[m.Index..];

            // Pragma adjacency is LINE-based, never a character window: a fixed
            // window let one legitimate pragma silently exempt the NEXT,
            // unrelated statement (council r1, carto #1, reproduced), and a
            // semicolon-bounded window truncated away pragmas whose own reason
            // text contained ';'. Accepted positions: the occurrence's own line
            // (before the occurrence), or the contiguous run of comment-only /
            // blank lines directly above it — the first CODE line breaks the
            // run. The pragma therefore sits immediately above (or on) the line
            // that names the DbSet.
            var pragma = PragmaCovers(source, m.Index);

            if (!WindowIsScoped(window) && !pragma)
            {
                var line = source[..m.Index].Count(c => c == '\n') + 1;
                var snippet = Regex.Replace(window, @"\s+", " ");
                violations.Add(new Violation(fileLabel, line, snippet[..Math.Min(140, snippet.Length)]));
            }
        }
        return violations;
    }

    internal static IReadOnlyCollection<string> AllDbSets()
    {
        return typeof(ApplicationDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.Name)
            .ToList();
    }

    internal static IReadOnlyCollection<string> TenantDbSets()
    {
        // Name.Contains, not an exact match: HouseholdConnection scopes via
        // HouseholdId1/HouseholdId2 and fell entirely outside an exact-name
        // derivation (council r1, opus #2).
        return typeof(ApplicationDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)
                        && p.PropertyType.GetGenericArguments()[0].GetProperties()
                            .Any(q => q.Name.Contains("HouseholdId")))
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

    private static readonly string[] ScannedExtensions = [".cs", ".razor", ".cshtml"];

    private static IEnumerable<string> AppSourceFiles()
    {
        var root = AppSourceRoot();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
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
        sets.Should().Contain(["Recipes", "Chores", "ShoppingLists", "Users", "HouseholdConnections"]);
        // Household itself is NOT a tenant entity — it IS the tenant.
        sets.Should().NotContain("Households");
    }

    // The complement is pinned EXACTLY (council r1, opus #2 — "a universal
    // negative claim is an inventory in disguise"): a new DbSet cannot land on
    // the unguarded side without a deliberate edit to this list and a reason.
    [Fact]
    public void The_unguarded_complement_is_exactly_the_reviewed_set()
    {
        var excluded = AllDbSets().Except(TenantDbSets()).OrderBy(n => n).ToList();
        excluded.Should().BeEquivalentTo(new[]
        {
            "HouseholdRequests", // pre-membership onboarding — no household exists yet
            "Households",        // IS the tenant, not scoped by one
        });
    }

    // A file that creates a context MUST yield at least one receiver, or its
    // queries are invisible to the scan — the sync-factory blindness class
    // (council r1, opus #3: SeedData.cs was covered only by naming luck).
    [Fact]
    public void Every_context_creating_file_yields_receivers()
    {
        var root = AppSourceRoot();
        var blind = AppSourceFiles()
            .Select(f => new { File = f, Source = File.ReadAllText(f) })
            .Where(x => x.Source.Contains("CreateDbContext") && Receivers(x.Source).Count == 0)
            .Select(x => Path.GetRelativePath(root, x.File).Replace('\\', '/'))
            .ToList();
        blind.Should().BeEmpty("a file that creates a DbContext but yields no receiver is scanned as if it had no queries");
    }

    [Fact]
    public void The_scan_visits_a_substantial_occurrence_population()
    {
        var tenantSets = TenantDbSets();
        var total = 0;
        foreach (var file in AppSourceFiles())
        {
            var source = File.ReadAllText(file);
            var receivers = Receivers(source); // the SAME detection Scan uses — the two cannot diverge
            if (receivers.Count == 0) continue;
            var re = new Regex($@"\b(?:{string.Join("|", receivers)})\s*\.\s*(?:{string.Join("|", tenantSets)})\b");
            total += re.Matches(source).Count;
        }
        // 216 measured at adoption (2026-08-29), before the derivation widened.
        // Shrinking dramatically below that means the scan stopped seeing the
        // codebase, not that the codebase stopped querying.
        total.Should().BeGreaterThan(150);
    }

    // ── The scanner's structural blind spots are banned patterns ────────────
    // The occurrence scan anchors on DbSet PROPERTY names, so `context.Set<T>()`
    // and raw SQL reach entities without ever naming a DbSet — invisible to the
    // scan by construction. Neither appears in app source today (measured
    // 2026-08-29); this fact keeps it that way. A future legitimate use carries
    // a reasoned TENANT-SCOPE-OK pragma on the same line or the line above, and
    // a reviewer's eyes. ApplicationDbContext.cs is excluded: its DbSet property
    // bodies are the one legitimate home of bare Set<T>() calls.

    [Fact]
    public void Scanner_blind_spot_patterns_do_not_appear_unreviewed()
    {
        var offenders = new List<string>();
        var root = AppSourceRoot();
        var banned = new Regex(@"\.Set\s*<|FromSql|SqlQuery|ExecuteSql");

        foreach (var file in AppSourceFiles().Where(f => !f.EndsWith("ApplicationDbContext.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (banned.IsMatch(lines[i])
                    && !Regex.IsMatch(lines[i], @"TENANT-SCOPE-OK:[^\S\n]*\S")
                    && !(i > 0 && Regex.IsMatch(lines[i - 1], @"TENANT-SCOPE-OK:[^\S\n]*\S")))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "`Set<T>()` and raw SQL bypass the tenant-scope scan by construction — " +
            "use the DbSet properties, or carry a reasoned // TENANT-SCOPE-OK: pragma. Offenders:\n" +
            string.Join("\n", offenders));
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
    public void NC_a_projection_mention_of_HouseholdId_is_not_scope()
    {
        // The cross-tenant dump wearing the right word (council r1, all lenses):
        // HouseholdId appears, but only as a projected column — no comparison.
        const string projection = """
            var context = await factory.CreateDbContextAsync();
            var dump = await context.Recipes.Select(r => new { r.Id, r.HouseholdId }).ToListAsync();
            """;
        Scan(projection, "nc.cs", NcSets).Should().ContainSingle();
    }

    [Fact]
    public void NC_a_pragma_does_not_leak_onto_the_following_statement()
    {
        // Reproduced by council r1 (carto #1) against the fixed-character
        // lookback: one legitimate pragma must not exempt the NEXT statement.
        const string leak = """
            var context = await factory.CreateDbContextAsync();
            // TENANT-SCOPE-OK: legit reason for the first read
            var a = await context.Recipes.Where(r => r.HouseholdId == hid).ToListAsync();
            var b = await context.Recipes.ToListAsync();
            """;
        var flags = Scan(leak, "nc.cs", NcSets);
        flags.Should().ContainSingle();
        flags[0].Line.Should().Be(4);
    }

    [Fact]
    public void NC_a_qualified_factory_receiver_is_detected()
    {
        // `this.dbFactory` — the qualified form the first receiver regex missed
        // (council r1 challenge residual: pin it so `[\w.]+` cannot regress).
        const string qualified = """
            await using var context = await this.dbFactory.CreateDbContextAsync();
            var all = await context.Recipes.ToListAsync();
            """;
        Scan(qualified, "nc.cs", NcSets).Should().ContainSingle();
    }

    [Fact]
    public void NC_a_sync_factory_context_is_still_scanned()
    {
        // The sync CreateDbContext() used to yield NO receivers, silently
        // skipping the whole file (council r1, opus #3).
        const string sync = """
            using var context = factory.CreateDbContext();
            var all = await context.Recipes.ToListAsync();
            """;
        Scan(sync, "nc.cs", NcSets).Should().ContainSingle();
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
