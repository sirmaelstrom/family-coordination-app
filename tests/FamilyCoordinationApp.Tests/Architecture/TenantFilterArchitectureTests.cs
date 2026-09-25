using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// The read switch's guard facts (fca-household-scope WP-02 step 6; decisions D7). Each scans the comment- and
/// string-stripped <c>src</c> (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>), <c>Tenancy/</c>
/// included: its definitions don't match (the doc and message mentions are stripped, and a definition has no <c>.</c>
/// receiver), so no file is excluded. Each ships an in-tree negative control that feeds its detector known-bad input.
/// <list type="bullet">
/// <item><b>Fact 1:</b> no bare <c>IgnoreQueryFilters()</c>: it would drop <c>"Tenant"</c> along with soft delete.</item>
/// <item><b>Fact 2:</b> every <c>IgnoreQueryFilters(…)</c> whose argument is not exactly <c>["SoftDelete"]</c> is a
/// Tenant bypass. It needs a <c>// TENANT-SCOPE-OK:</c> pragma in the comment run directly above the FIRST line of its
/// statement, and each bypass matches exactly one <c>bypass:</c> row of <c>.planning/tenancy-bypass-inventory.md</c>
/// (and each row one bypass) by its site id: the file, the enclosing member and a fingerprint of the statement
/// (<see cref="BypassSiteId"/>). A line shift keeps the id; a changed, moved or substituted bypass doesn't. Treating
/// any non-soft-delete argument as a bypass (a variable, <c>new[] { … }</c>) is deliberate: a list the scan can't
/// read is not a list it can clear.</item>
/// <item><b>Fact 4:</b> the <c>.RunAs(</c> calls per file are exactly the D11 allowlist's counts (every match counts,
/// two on one line are two).</item>
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
    /// <summary>
    /// The D11 <c>RunAs</c> callers and how many calls each makes. A new call is a deliberate edit here.
    /// <para><b>Accepted limit:</b> a same-count substitution inside an allowlisted file (one call removed, another
    /// added) passes. The count catches an extra call; it doesn't say which call.</para>
    /// </summary>
    internal static readonly Dictionary<string, int> RunAsCallsPerFile = new(StringComparer.Ordinal)
    {
        ["Data/SeedData.cs"] = 1,                      // the dev seed, from the Users lookup to the end
        ["Endpoints/CalendarTokenEndpoints.cs"] = 1,   // the anonymous feed, around its Task.WhenAll
        ["Services/Digest/DigestService.cs"] = 1,      // per swept household
        ["Services/HouseholdRequestService.cs"] = 2,   // admin approve, admin create
        ["Services/LoginProfileService.cs"] = 1,       // the login profile write
        ["Services/SetupService.cs"] = 1,              // first-run setup
    };

    /// <summary>Tenant entities that deliberately have no Tenant filter (D5): a nullable dual-mode id and a pair.</summary>
    internal static readonly Type[] UnfilteredHouseholdIdEntities = [typeof(Feedback), typeof(HouseholdConnection)];

    private static readonly Regex IgnoreQueryFiltersCallRe = new(@"\bIgnoreQueryFilters\s*\(");
    private static readonly Regex SoftDeleteOnlyArgumentRe = new(@"^\s*\[\s*""SoftDelete""\s*\]\s*$");
    private static readonly Regex RunAsCallRe = new(@"\.\s*RunAs\s*\(");

    // ── The detectors (pure; the negative controls exercise them) ────────────

    /// <summary>
    /// One <c>IgnoreQueryFilters</c> call: its argument text (from the ORIGINAL source), its statement's start line,
    /// the member that encloses it, and the statement's fingerprint (<see cref="BypassSiteId"/>).
    /// </summary>
    internal sealed record FilterCall(
        int Line, int StatementLine, string Argument, bool PragmaAboveStatement, string Member, string Fingerprint, string Statement)
    {
        public bool IsBare => Argument.Trim().Length == 0;
        public bool IsTenantBypass => !IsBare && !SoftDeleteOnlyArgumentRe.IsMatch(Argument);

        /// <summary>The site id an inventory row carries: <c>Member#fingerprint</c>.</summary>
        public string SiteId => $"{Member}#{Fingerprint}";
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
            var statement = code[start..StatementEnd(code, start, close)];
            calls.Add(new FilterCall(
                LineOf(code, m.Index),
                LineOf(code, start),
                argument,
                TenantScopeArchitectureTests.PragmaCovers(source, start),
                EnclosingMember(code, start),
                BypassSiteId(statement),
                Regex.Replace(statement, @"\s+", " ").Trim()));
        }
        return calls;
    }

    /// <summary>
    /// The fingerprint half of a bypass's site id: the first 8 hex digits of SHA-256 over the statement's stripped
    /// text with ALL whitespace removed. Comments and string contents are blanked by the stripper, so editing a
    /// comment, re-flowing the chain across lines, or shifting the statement up or down keeps the id; changing the
    /// statement's code changes it. (Stated limit: a change only inside a string literal of the statement keeps it.)
    /// </summary>
    internal static string BypassSiteId(string strippedStatement) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(strippedStatement, @"\s+", ""))))[..8];

    /// <summary>
    /// The exclusive end of the statement that starts at <paramref name="start"/>: its <c>;</c> at nesting depth 0, or
    /// a block-opening <c>{</c> at depth 0 after the call (an <c>if (…) {</c> header), or the enclosing block's close.
    /// </summary>
    internal static int StatementEnd(string code, int start, int callClose)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (depth == 0 && c == ';') return i + 1;
            if (depth == 0 && c == '{' && i > callClose) return i;
            if (c is '(' or '[' or '{') depth++;
            else if ((c is ')' or ']' or '}') && --depth < 0) return i;
        }
        return code.Length;
    }

    // The first identifier (not a modifier) directly before `(`, past one balanced type-parameter list: `M<T>(`.
    private static readonly Regex MemberNameRe = new(
        @"\b(?!(?:public|private|protected|internal|static|async|override|virtual|sealed|abstract|partial|readonly|extern|unsafe|new)\b)" +
        @"(?<name>\w+)\s*(?:<(?>[^<>]+|<(?<d>)|>(?<-d>))*(?(d)(?!))>)?\s*\(");

    private static readonly Regex ControlHeaderRe = new(
        @"^(?:await\s+)?(?:if|else|for|foreach|while|do|switch|try|catch|finally|using|lock|fixed|unsafe|checked|unchecked|delegate)\b");

    /// <summary>
    /// The name of the member whose body holds <paramref name="index"/>: the nearest enclosing block whose header is a
    /// method, local function or constructor (a lambda, control statement, accessor or initializer block is walked
    /// out of), else the enclosing type's name.
    /// </summary>
    internal static string EnclosingMember(string code, int index)
    {
        var i = index;
        while (true)
        {
            var open = EnclosingOpenBrace(code, i);
            if (open < 0) return "(file)";
            var headerStart = open == 0 ? 0 : code.LastIndexOfAny([';', '{', '}'], open - 1) + 1;
            var header = Regex.Replace(Regex.Replace(code[headerStart..open], @"\s+", " ").Trim(), @"^(?:\[[^\]]*\]\s*)+", "");
            var type = Regex.Match(header, @"\b(?:class|record|struct|interface)\s+(\w+)");
            if (type.Success) return type.Groups[1].Value;
            if (!header.EndsWith("=>", StringComparison.Ordinal) && !ControlHeaderRe.IsMatch(header)
                && !Regex.IsMatch(header, @"\bnew\b"))
            {
                var name = MemberNameRe.Match(header);
                if (name.Success) return name.Groups["name"].Value;
            }
            i = open;
        }
    }

    /// <summary>The index of the unmatched <c>{</c> before <paramref name="index"/>, or -1 at file level.</summary>
    private static int EnclosingOpenBrace(string code, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            if (code[i] == '}') depth++;
            else if (code[i] == '{' && depth-- == 0) return i;
        }
        return -1;
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

    /// <summary>The line of every <c>.RunAs(</c> call, one entry per match: two calls on one line are two.</summary>
    internal static List<int> RunAsCallLines(string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        return RunAsCallRe.Matches(code).Select(m => LineOf(code, m.Index)).ToList();
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
                        && !f.Path.StartsWith("obj/", StringComparison.Ordinal))
            .Select(f => (f.Path, File.ReadAllText(f.Full)))
            .ToList();
    }

    private static List<(string File, FilterCall Call)> AllFilterCalls() =>
        AppSources().SelectMany(f => FilterCalls(f.Source).Select(c => (f.RelativePath, c))).ToList();

    /// <summary>One <c>bypass:</c> row: its <c>file:line</c> (for reviewers) and its site id (for the match).</summary>
    internal sealed record InventoryRow(string Location, string SiteId)
    {
        public string File => Location[..Location.LastIndexOf(':')];
    }

    /// <summary>
    /// The inventory's <c>bypass:</c> rows. The first cell is <c>`file:line` `Member#fingerprint`</c>; a row with no
    /// site id parses with an empty one, which matches no bypass.
    /// </summary>
    internal static List<InventoryRow> InventoryBypassRows(string markdown) =>
        Regex.Matches(markdown, @"^\|\s*`(?<loc>[^`]+:\d+)`(?:\s*`(?<site>[^`]*)`)?\s*\|.*?\|\s*bypass:", RegexOptions.Multiline)
            .Select(m => new InventoryRow(m.Groups["loc"].Value, m.Groups["site"].Value))
            .ToList();

    /// <summary>
    /// Fact 2's matcher: each bypass pairs with one row of the same file and site id, as multisets. Returns the
    /// bypasses left with no row (unlisted) and the rows left with no bypass (stale), both empty when they match.
    /// </summary>
    internal static (List<string> Unlisted, List<string> Stale) MatchBypassesToRows(
        IEnumerable<(string File, FilterCall Call)> bypasses, IEnumerable<InventoryRow> rows)
    {
        var open = rows.GroupBy(r => (r.File, r.SiteId)).ToDictionary(g => g.Key, g => new Queue<InventoryRow>(g));
        var unlisted = new List<string>();
        foreach (var (file, call) in bypasses)
        {
            if (open.TryGetValue((file, call.SiteId), out var queue) && queue.Count > 0)
                queue.Dequeue();
            else
                unlisted.Add($"{file}:{call.StatementLine} `{call.SiteId}`  {call.Statement}");
        }
        var stale = open.Values.SelectMany(q => q).Select(r => $"{r.Location} `{r.SiteId}`").ToList();
        return (unlisted, stale);
    }

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

    /// <summary>
    /// Each bypass matches exactly one <c>bypass:</c> row, and each row one bypass, by site id: the file, the enclosing
    /// member and the statement fingerprint (<see cref="BypassSiteId"/>). A same-file substitution (one bypass removed,
    /// another added, the count unchanged) leaves an unlisted bypass and a stale row, and fails.
    /// <para><b>What still passes, and why:</b> a line match would fail CI on every unrelated edit that shifts a
    /// bypass, which trains people to regenerate the inventory blindly. The id ignores lines, whitespace, re-flowing
    /// and comments, so blank lines, comments, pragma rewording and edits elsewhere in the file or method pass. Editing
    /// the bypass statement itself, renaming its member or moving it changes the id: that is the review point, and the
    /// failure prints the new id beside the statement. Rows still carry <c>file:line</c>, for reviewers; a stale line
    /// number doesn't fail.</para>
    /// <para><b>Stated limits:</b> two textually identical bypass statements in one member share an id and match as a
    /// count; a change only inside a string literal of the statement keeps the id (strings are blanked).</para>
    /// </summary>
    [Fact]
    public void Fact2_each_Tenant_bypass_in_src_matches_its_own_inventory_bypass_row()
    {
        var bypasses = AllFilterCalls().Where(c => c.Call.IsTenantBypass).ToList();
        var rows = InventoryBypassRows(InventoryText());

        bypasses.Should().NotBeEmpty("the scan must see the bypasses the switch placed (guard the guard)");
        rows.Should().NotBeEmpty("the inventory parser must see its `bypass:` rows (guard the guard)");

        var (unlisted, stale) = MatchBypassesToRows(bypasses, rows);
        using var scope = new AssertionScope();
        unlisted.Should().BeEmpty(
            "every Tenant bypass in src needs its own `bypass:` row carrying its site id (M4, V6); add or re-point the " +
            "row after reviewing the gate. Unlisted:\n" + string.Join("\n", unlisted));
        stale.Should().BeEmpty(
            "every `bypass:` row must name a bypass in src by its site id; remove or re-point it. Stale:\n" +
            string.Join("\n", stale));
    }

    [Fact]
    public void Fact4_RunAs_is_called_only_from_the_allowlisted_files_with_the_allowlisted_counts()
    {
        var calls = AppSources()
            .Select(f => (f.RelativePath, Count: RunAsCallLines(f.Source).Count))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.RelativePath, f => f.Count, StringComparer.Ordinal);

        calls.Should().BeEquivalentTo(RunAsCallsPerFile,
            "RunAs is system work outside a caller: exactly the digest, the calendar feed, setup, admin approve/create, " +
            "the dev seed and the login profile write use it (D11), each with its counted calls. In src: " +
            string.Join(", ", calls.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")));
    }

    [Fact]
    public void Fact5_every_tenant_entity_has_the_Tenant_filter_and_the_tenant_set_is_the_name_derived_set()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"fact5-{Guid.NewGuid():N}").Options);

        var tenantTypes = db.Model.GetEntityTypes().Count(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType));

        using var scope = new AssertionScope(); // report the coverage reasons even when the count also fails
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
    public void NC_fact2_inventory_rows_are_parsed_by_classification_with_their_site_ids()
    {
        const string markdown = """
            | file:line | expression (trimmed) | classification | gate / household source |
            |---|---|---|---|
            | `Services/A.cs:10` `Find#0a1b2c3d` | `Users.Where(…)` | bypass: identity resolution | the email claim |
            | `Services/A.cs:20` | `x.HouseholdId == householdId` | caller-scoped (no change) | caller |
            | `Services/B.cs:5` | `RunAs(h)` | RunAs: the swept household | rows |
            | `Services/B.cs:9` | `Recipes.IgnoreQueryFilters(["Tenant"])` | bypass: connected recipes | AreHouseholdsConnectedAsync |
            """;

        InventoryBypassRows(markdown).Should().Equal(
            new InventoryRow("Services/A.cs:10", "Find#0a1b2c3d"),
            new InventoryRow("Services/B.cs:9", ""));
    }

    // The same-file substitution fact 2's per-file count missed (PR #121 review 1, codex): Remove() loses its bypass
    // and Other() gains one under a verbatim copy of Remove()'s pragma, so the file's count is unchanged.
    private const string MatcherBefore = """
        class S
        {
            async Task<bool> Remove(string email)
            {
                // TENANT-SCOPE-OK: gated by X at Y.cs:1
                return await context.Users.IgnoreQueryFilters(["Tenant"]).AnyAsync(u => u.Email == email);
            }

            async Task<User?> Other(int id)
            {
                return await context.Users.FirstOrDefaultAsync(u => u.Id == id);
            }

            async Task<bool> Keep(string email)
            {
                // TENANT-SCOPE-OK: gated by X at Y.cs:2
                return await context.Users.IgnoreQueryFilters(["Tenant"]).AnyAsync(u => u.Email == email);
            }
        }
        """;

    private const string MatcherSubstituted = """
        class S
        {
            async Task<bool> Remove(string email)
            {
                return await context.Users.AnyAsync(u => u.Email == email);
            }

            async Task<User?> Other(int id)
            {
                // TENANT-SCOPE-OK: gated by X at Y.cs:1
                return await context.Users.IgnoreQueryFilters(["Tenant"]).FirstOrDefaultAsync(u => u.Id == id);
            }

            async Task<bool> Keep(string email)
            {
                // TENANT-SCOPE-OK: gated by X at Y.cs:2
                return await context.Users.IgnoreQueryFilters(["Tenant"]).AnyAsync(u => u.Email == email);
            }
        }
        """;

    // A pure line shift and a re-flow: blank lines and comments above, the chain split across lines.
    private const string MatcherShifted = """
        class S
        {

            // an unrelated comment

            async Task<bool> Remove(string email)
            {


                // TENANT-SCOPE-OK: gated by X at Y.cs:1, reworded
                return await context.Users
                    .IgnoreQueryFilters(["Tenant"])
                    .AnyAsync(u => u.Email == email);
            }

            async Task<User?> Other(int id)
            {
                return await context.Users.FirstOrDefaultAsync(u => u.Id == id);
            }

            async Task<bool> Keep(string email)
            {
                // a new note
                // TENANT-SCOPE-OK: gated by X at Y.cs:2
                return await context.Users.IgnoreQueryFilters(["Tenant"]).AnyAsync(u => u.Email == email);
            }
        }
        """;

    private static List<(string File, FilterCall Call)> Bypasses(string file, string source) =>
        FilterCalls(source).Where(c => c.IsTenantBypass).Select(c => (file, c)).ToList();

    [Fact]
    public void NC_fact2_the_matcher_flags_a_same_file_substitution_and_a_stale_row_and_passes_a_line_shift()
    {
        var before = Bypasses("Services/S.cs", MatcherBefore);
        before.Select(b => b.Call.Member).Should().Equal(["Remove", "Keep"], "the member is the enclosing method");
        before[0].Call.SiteId.Should().NotBe(before[1].Call.SiteId,
            "the same statement text in two methods is two sites: the member is part of the id");
        var rows = before.Select(b => new InventoryRow($"Services/S.cs:{b.Call.StatementLine}", b.Call.SiteId)).ToList();

        var same = MatchBypassesToRows(before, rows);
        same.Unlisted.Should().BeEmpty();
        same.Stale.Should().BeEmpty();

        var shifted = MatchBypassesToRows(Bypasses("Services/S.cs", MatcherShifted), rows);
        shifted.Unlisted.Should().BeEmpty("a line shift, a re-flow and comment edits keep every site id");
        shifted.Stale.Should().BeEmpty();

        var substituted = MatchBypassesToRows(Bypasses("Services/S.cs", MatcherSubstituted), rows);
        substituted.Unlisted.Should().ContainSingle().Which.Should().StartWith("Services/S.cs:11 `Other#",
            "the added bypass has no row, although its pragma is a verbatim copy and the file's count is unchanged");
        substituted.Stale.Should().Equal([$"Services/S.cs:6 `{before[0].Call.SiteId}`"], "Remove()'s row names nothing now");

        var withGhost = rows.Append(new InventoryRow("Services/S.cs:40", "Gone#00000000")).ToList();
        MatchBypassesToRows(before, withGhost).Stale.Should().Equal(["Services/S.cs:40 `Gone#00000000`"],
            "a row whose site doesn't exist is stale");
        var idless = MatchBypassesToRows(before, [rows[0], rows[1] with { SiteId = "" }]);
        idless.Unlisted.Should().ContainSingle().Which.Should().StartWith($"Services/S.cs:17 `{before[1].Call.SiteId}`",
            "a row without a site id matches nothing");
        idless.Stale.Should().Equal(["Services/S.cs:17 ``"]);
    }

    [Fact]
    public void NC_fact4_a_RunAs_call_is_seen_and_a_mention_is_not()
    {
        const string source = """
            // context.Tenant.RunAs(1) in a comment
            var s = "tenant.RunAs(2)";
            using var scope = context.Tenant
                .RunAs(householdId);
            using var a = tenant.RunAs(1); using var b = tenant.RunAs(2);
            """;

        RunAsCallLines(source).Should().Equal([4, 5, 5], "every call counts, two on one line are two");
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
