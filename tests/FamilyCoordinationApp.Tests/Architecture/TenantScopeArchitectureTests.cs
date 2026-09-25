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
/// projection-passing reads the first scan missed), re-counted 2026-09-25 by
/// `grep -rn 'TENANT-SCOPE-OK:' src --include=*.cs` @ 240dc28: 21 pragmas — auth/identity
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
    //
    // ONE exemption (fca-household-scope WP-01, pre-authorized at council r1):
    // Tenancy/TenantDbContextFactory.cs DEFINES CreateDbContext() and never
    // receives or queries a context. Exempted by exact path, never by pattern.
    internal static readonly string[] ReceiverExemptFiles =
    [
        "Tenancy/TenantDbContextFactory.cs", // defines the factory, never receives a context
    ];

    internal static List<string> BlindContextCreatingFiles(IEnumerable<(string RelativePath, string Source)> files) =>
        files
            .Where(f => !ReceiverExemptFiles.Contains(f.RelativePath))
            .Where(f => f.Source.Contains("CreateDbContext") && Receivers(f.Source).Count == 0)
            .Select(f => f.RelativePath)
            .ToList();

    [Fact]
    public void Every_context_creating_file_yields_receivers()
    {
        var root = AppSourceRoot();
        var blind = BlindContextCreatingFiles(AppSourceFiles()
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f))));
        blind.Should().BeEmpty("a file that creates a DbContext but yields no receiver is scanned as if it had no queries");
    }

    [Fact]
    public void NC_the_receiver_exemption_covers_exactly_the_factory_file()
    {
        const string definesFactory = """
            public ApplicationDbContext CreateDbContext() => Build();
            """;
        BlindContextCreatingFiles(
        [
            ("Tenancy/TenantDbContextFactory.cs", definesFactory),
            ("Services/SomeOtherFactory.cs", definesFactory),
        ]).Should().Equal(["Services/SomeOtherFactory.cs"],
            "the exemption is the factory's exact path; any OTHER receiver-less file defining CreateDbContext is still blind");
    }

    // ── Fact 3 (fca-household-scope D7/D12): contexts are built only by the factory ──
    // The options-only constructor gives an Unfiltered tenant (D12). Banning every way
    // to construct a context in src confines that hole to the tests: production code
    // can only get a context from TenantDbContextFactory, which always passes the
    // scope's tenant.
    //
    // The scan runs on the WHOLE file after StripCommentsAndStrings has blanked every
    // comment and string/char literal (keeping newlines, so an offset still maps to
    // its line): docs and strings may NAME a banned form, and `\s` spans newlines, so
    // a construction split across lines is still one match, reported at its start.
    // One named rule per form (PR #120 review 1, astra/codex/opus):
    //  - explicit-new: `new ApplicationDbContext(`, qualified or not, `(` on any line.
    //  - target-typed-declaration: `ApplicationDbContext[?] x = new(` as a local,
    //    field, or property initializer (`{ get; } = new(`), qualified or not.
    //  - target-typed-return: `=> new(` on a method, local function or property
    //    typed ApplicationDbContext[?] or Task/ValueTask of it, and every
    //    `return new(` inside such a member's braces. A nested lambda that returns
    //    another type false-flags, which fails safe.
    //  - typed-lambda: `ApplicationDbContext (o) => new(o)`, and a delegate-typed
    //    variable whose last type argument is ApplicationDbContext, initialized by
    //    a lambda that returns `new(`.
    //  - activation: `CreateInstance<ApplicationDbContext>`,
    //    `GetServiceOrCreateInstance<ApplicationDbContext>`, and any
    //    `typeof(ApplicationDbContext)` except one followed by `.Assembly`
    //    (OnModelCreating reads the assembly for ApplyConfigurationsFromAssembly).
    //  - subclass: a class or record whose base list starts with
    //    ApplicationDbContext, the primary-constructor form included.
    //  - options-type: any mention of DbContextOptions<ApplicationDbContext> or
    //    DbContextOptionsBuilder<ApplicationDbContext>. The backstop for target-typed
    //    `new(opts)` in argument position or an assignment, and for casts: a context
    //    can't be built without options of that type, and naming the type trips
    //    this rule. It has its own allow-list: the context, which declares the
    //    constructors, and the factory, which receives the options from DI.
    // Every rule but options-type exempts ContextConstructionAllowedFiles (the
    // factory only); options-type exempts OptionsTypeAllowedFiles.
    //
    // Residual limits (what this scan does NOT see):
    //  - a construction inside an interpolation hole is blanked with its string;
    //  - reflection by string name (`Type.GetType("…")`) isn't caught, nor are
    //    options reached without naming their type (`dynamic`, the non-generic base);
    //  - Migrations/ is excluded (AppSourceFiles), so a factory there is invisible;
    //  - .razor/.cshtml markup is lexed as C#: an apostrophe in markup blanks the
    //    rest of its line;
    //  - DI-activated contexts (AddDbContext<ApplicationDbContext> and similar) are
    //    covered by TenantPlumbingTests' DI assertions, not by this scan.
    // No IDesignTimeDbContextFactory exists (checked 2026-09-24); if one is added it
    // gets an explicit entry here.

    internal static readonly string[] ContextConstructionAllowedFiles =
    [
        "Tenancy/TenantDbContextFactory.cs",
    ];

    internal const string OptionsTypeRule = "options-type";

    internal static readonly string[] OptionsTypeAllowedFiles =
    [
        "Data/ApplicationDbContext.cs",      // declares the constructors that take the options
        "Tenancy/TenantDbContextFactory.cs", // receives the options from DI
    ];

    /// <summary>
    /// Blanks every comment (<c>//</c>, <c>///</c>, <c>/* */</c>) and every string or char literal (regular,
    /// verbatim, interpolated, raw) to spaces, keeping newlines and length so an offset still maps to its line.
    /// An interpolation hole is blanked with its string.
    /// </summary>
    internal static string StripCommentsAndStrings(string source)
    {
        var chars = source.ToCharArray();
        var i = 0;
        while (i < source.Length)
        {
            var end = CommentOrLiteralEnd(source, i);
            if (end == i)
            {
                i++;
                continue;
            }
            for (var k = i; k < end; k++)
                if (chars[k] is not ('\n' or '\r')) chars[k] = ' ';
            i = end;
        }
        return new string(chars);
    }

    // The exclusive end of the comment or literal that starts at i, or i when none starts there.
    private static int CommentOrLiteralEnd(string s, int i)
    {
        char At(int k) => k < s.Length ? s[k] : '\0';

        if (s[i] == '/' && At(i + 1) == '/')
        {
            var newline = s.IndexOf('\n', i);
            return newline < 0 ? s.Length : newline;
        }
        if (s[i] == '/' && At(i + 1) == '*')
        {
            var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + 2;
        }
        if (s[i] == '\'') return QuotedEnd(s, i + 1, '\'', interpolated: false);

        // String prefixes: $…, @, $…@, @$…
        var j = i;
        var interpolated = false;
        var verbatim = false;
        while (At(j) == '$') { interpolated = true; j++; }
        if (At(j) == '@')
        {
            verbatim = true;
            j++;
            while (At(j) == '$') { interpolated = true; j++; }
        }
        if (At(j) != '"') return i;

        var quotes = 0;
        while (At(j + quotes) == '"') quotes++;
        if (!verbatim && quotes >= 3) // raw: ends at the next run of as many quotes
        {
            var close = s.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + quotes;
        }
        return verbatim ? VerbatimEnd(s, j + 1, interpolated) : QuotedEnd(s, j + 1, '"', interpolated);
    }

    // A regular string or a char literal: backslash escapes; unterminated at the end of its line.
    private static int QuotedEnd(string s, int k, char quote, bool interpolated)
    {
        while (k < s.Length)
        {
            var c = s[k];
            if (c == '\\') { k += 2; continue; }
            if (c == quote) return k + 1;
            if (c == '\n') return k;
            if (interpolated && c == '{')
            {
                if (k + 1 < s.Length && s[k + 1] == '{') { k += 2; continue; }
                k = HoleEnd(s, k + 1);
                continue;
            }
            k++;
        }
        return s.Length;
    }

    // A verbatim string: `""` escapes a quote; newlines are content.
    private static int VerbatimEnd(string s, int k, bool interpolated)
    {
        while (k < s.Length)
        {
            var c = s[k];
            if (c == '"')
            {
                if (k + 1 < s.Length && s[k + 1] == '"') { k += 2; continue; }
                return k + 1;
            }
            if (interpolated && c == '{')
            {
                if (k + 1 < s.Length && s[k + 1] == '{') { k += 2; continue; }
                k = HoleEnd(s, k + 1);
                continue;
            }
            k++;
        }
        return s.Length;
    }

    // Just past the '}' that closes an interpolation hole whose body starts at k. Nested strings, chars and
    // comments are skipped whole, so a quote or brace inside them can't end the hole early.
    private static int HoleEnd(string s, int k)
    {
        var depth = 0;
        while (k < s.Length)
        {
            var end = CommentOrLiteralEnd(s, k);
            if (end > k)
            {
                k = end;
                continue;
            }
            if (s[k] == '{') depth++;
            else if (s[k] == '}')
            {
                if (depth == 0) return k + 1;
                depth--;
            }
            k++;
        }
        return s.Length;
    }

    // An optionally qualified ApplicationDbContext type name (`Data.`, `global::FamilyCoordinationApp.Data.`).
    private const string CtxType = @"(?:global\s*::\s*)?(?:\w+\s*\.\s*)*ApplicationDbContext\b";
    // A type name starts here: not mid-identifier, not after a qualifier.
    private const string TypeStart = @"(?<![\w.:])";
    private const string NullableMark = @"(?:\s*\?)?";
    // A member's declared type: ApplicationDbContext[?], or Task/ValueTask of it.
    private const string CtxOrTaskOfCtx =
        @"(?:(?:global\s*::\s*)?(?:\w+\s*\.\s*)*(?:Value)?Task\s*<\s*" + CtxType + NullableMark + @"\s*>|" + CtxType + ")" + NullableMark;
    private const string ParamList = @"\((?:[^()]|\([^()]*\))*\)";
    // A method or local function's signature tail: type parameters, parameters, constraints.
    private const string SignatureTail = @"(?:<[^<>]*>)?\s*" + ParamList + @"(?:\s*where\b[^{};=]*)?";
    // A type argument before the last one, with up to two levels of nested generics.
    private const string TypeArg = @"(?:[^<>;{}()=,]|<(?:[^<>;{}()=]|<[^<>;{}()=]*>)*>)+";

    private static readonly Regex ExplicitNewRe = new(@"\bnew\s+" + CtxType + @"\s*\(");

    private static readonly Regex TargetTypedDeclarationRe =
        new(TypeStart + CtxType + NullableMark + @"\s+\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\(");

    private static readonly Regex ExpressionBodiedMemberRe =
        new(TypeStart + CtxOrTaskOfCtx + @"\s+\w+\s*(?:" + SignatureTail + @")?\s*=>\s*new\s*\(");

    // Ends at the member's opening brace; the body is brace-matched from there.
    private static readonly Regex BlockBodiedMemberRe =
        new(TypeStart + CtxOrTaskOfCtx + @"\s+\w+\s*(?:" + SignatureTail + @")?\s*\{");

    private static readonly Regex ReturnNewRe = new(@"\breturn\s+new\s*\(");

    // Ends at `new(` (expression body) or `{` (block body).
    private static readonly Regex ExplicitReturnLambdaRe =
        new(TypeStart + CtxType + NullableMark + @"\s*" + ParamList + @"\s*=>\s*(?:new\s*\(|\{)");

    private static readonly Regex DelegateVariableRe =
        new(TypeStart + @"(?:global\s*::\s*)?(?:\w+\s*\.\s*)*\w+\s*<\s*(?:" + TypeArg + @",\s*)*" + CtxType + NullableMark +
            @"\s*>" + NullableMark + @"\s+\w+\s*=\s*(?:static\s+)?(?:async\s+)?(?:\w+|" + ParamList + @")\s*=>\s*(?:new\s*\(|\{)");

    private static readonly Regex ActivationRe =
        new(@"\b(?:CreateInstance|GetServiceOrCreateInstance)\s*<\s*" + CtxType + @"\s*>" +
            @"|\btypeof\s*\(\s*" + CtxType + @"\s*\)(?!\s*\.\s*Assembly\b)");

    private static readonly Regex SubclassRe =
        new(@"\b(?:class|record)\s+\w+\s*(?:<[^<>]*>)?\s*(?:" + ParamList + @")?\s*:\s*" + CtxType);

    private static readonly Regex OptionsTypeRe =
        new(TypeStart + @"(?:global\s*::\s*)?(?:\w+\s*\.\s*)*DbContextOptions(?:Builder)?\s*<\s*" + CtxType + @"\s*>");

    /// <summary>
    /// Every context construction in <paramref name="source"/> as (line, rule), scanned on the whole comment- and
    /// string-stripped text so a match may span lines; the line is where the match starts.
    /// </summary>
    internal static List<(int Line, string Rule)> ContextConstructions(string source)
    {
        var code = StripCommentsAndStrings(source);
        var hits = new List<(int Index, string Rule)>();

        void AddMatches(Regex re, string rule)
        {
            foreach (Match m in re.Matches(code)) hits.Add((m.Index, rule));
        }

        // A match ending in `{` opens a block body: flag each `return new(` inside it. Otherwise flag the match.
        void AddBodies(Regex re, string rule, bool blockOnly)
        {
            foreach (Match m in re.Matches(code))
            {
                var last = m.Index + m.Length - 1;
                if (code[last] != '{')
                {
                    if (!blockOnly) hits.Add((m.Index, rule));
                    continue;
                }
                var close = MatchingBrace(code, last);
                foreach (Match r in ReturnNewRe.Matches(code[..close], last))
                    hits.Add((r.Index, rule));
            }
        }

        AddMatches(ExplicitNewRe, "explicit-new");
        AddMatches(TargetTypedDeclarationRe, "target-typed-declaration");
        AddMatches(ExpressionBodiedMemberRe, "target-typed-return");
        AddBodies(BlockBodiedMemberRe, "target-typed-return", blockOnly: true);
        AddBodies(ExplicitReturnLambdaRe, "typed-lambda", blockOnly: false);
        AddBodies(DelegateVariableRe, "typed-lambda", blockOnly: false);
        AddMatches(ActivationRe, "activation");
        AddMatches(SubclassRe, "subclass");
        AddMatches(OptionsTypeRe, OptionsTypeRule);

        return hits
            .Distinct() // nested typed members brace-match the same `return new(`
            .Select(h => (Line: code[..h.Index].Count(c => c == '\n') + 1, h.Rule))
            .OrderBy(h => h.Line).ThenBy(h => h.Rule, StringComparer.Ordinal)
            .ToList();
    }

    // The index of the '}' matching the '{' at open (on stripped text, so no literal brace can miscount).
    private static int MatchingBrace(string code, int open)
    {
        var depth = 0;
        for (var k = open; k < code.Length; k++)
        {
            if (code[k] == '{') depth++;
            else if (code[k] == '}' && --depth == 0) return k;
        }
        return code.Length;
    }

    internal static List<string> ContextConstructionOffenders(IEnumerable<(string RelativePath, string Source)> files) =>
        files
            .SelectMany(f => ContextConstructions(f.Source)
                .Where(h => h.Rule == OptionsTypeRule
                    ? !OptionsTypeAllowedFiles.Contains(f.RelativePath)
                    : !ContextConstructionAllowedFiles.Contains(f.RelativePath))
                .Select(h => $"{f.RelativePath}:{h.Line} [{h.Rule}]"))
            .ToList();

    private static readonly Regex CreateUnfilteredCallRe = new(@"\bCreateUnfiltered\s*\(");

    internal static List<int> CodeLinesMatching(string source, Regex pattern)
    {
        var lines = source.Split('\n');
        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            if (pattern.IsMatch(lines[i])) hits.Add(i + 1);
        }
        return hits;
    }

    private static List<string> OffendersOutside(Regex pattern, string[] allowedFiles)
    {
        var root = AppSourceRoot();
        var offenders = new List<string>();
        foreach (var file in AppSourceFiles())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (allowedFiles.Contains(rel)) continue;
            offenders.AddRange(CodeLinesMatching(File.ReadAllText(file), pattern).Select(line => $"{rel}:{line}"));
        }
        return offenders;
    }

    [Fact]
    public void Fact3_ApplicationDbContext_is_constructed_only_by_the_tenant_factory()
    {
        var root = AppSourceRoot();
        var offenders = ContextConstructionOffenders(AppSourceFiles()
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f))));

        offenders.Should().BeEmpty(
            "construct contexts only through IDbContextFactory<ApplicationDbContext> (TenantDbContextFactory): a " +
            "directly built context has no request tenant. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Fact3_guard_the_guard_the_factory_itself_is_seen_constructing()
    {
        var factory = File.ReadAllText(Path.Combine(AppSourceRoot(), "Tenancy", "TenantDbContextFactory.cs"));
        ContextConstructions(factory).Where(h => h.Rule == "explicit-new").Should().HaveCount(2,
            "the scan must see the two sanctioned constructions (CreateDbContext, CreateDbContextAsync), or it is matching nothing");
    }

    [Fact]
    public void Fact3_guard_the_guard_the_context_is_seen_naming_its_options_type()
    {
        var context = File.ReadAllText(Path.Combine(AppSourceRoot(), "Data", "ApplicationDbContext.cs"));
        ContextConstructions(context).Where(h => h.Rule == OptionsTypeRule).Should().NotBeEmpty(
            "the options-type backstop must see the constructors' parameter type, or it is matching nothing");
    }

    // One row per construction form: the exact (line, rule) set each must yield.
    public static TheoryData<string, string, string[]> ConstructionForms => new()
    {
        {
            "explicit-new",
            """
            await using var db = new ApplicationDbContext(options);
            """,
            ["1 [explicit-new]"]
        },
        {
            "explicit-new-split-line",
            """
            var a = new ApplicationDbContext
                (options);
            var b = new
                ApplicationDbContext(options);
            """,
            ["1 [explicit-new]", "3 [explicit-new]"]
        },
        {
            "explicit-new-qualified",
            """
            var a = new Data.ApplicationDbContext(options);
            var b = new global::FamilyCoordinationApp.Data.ApplicationDbContext(options);
            """,
            ["1 [explicit-new]", "2 [explicit-new]"]
        },
        {
            "explicit-new-after-literals",
            """"
            var q = '"'; var s = @"x""y"; var t = $"{(z ? "a" : "b")}"; var u = """ raw " quote """;
            var db = new ApplicationDbContext(options);
            /* c */ var db2 = new ApplicationDbContext(options); // new ApplicationDbContext(x)
            """",
            ["2 [explicit-new]", "3 [explicit-new]"]
        },
        {
            "target-typed-declaration",
            """
            ApplicationDbContext a = new(options);
            ApplicationDbContext? b = new(options);
            private readonly ApplicationDbContext _c = new(options);
            public ApplicationDbContext D { get; } = new(options);
            Data.ApplicationDbContext e = new(options);
            """,
            [
                "1 [target-typed-declaration]", "2 [target-typed-declaration]", "3 [target-typed-declaration]",
                "4 [target-typed-declaration]", "5 [target-typed-declaration]",
            ]
        },
        {
            "target-typed-return-expression",
            """
            ApplicationDbContext Make() => new(options);
            public static ApplicationDbContext? MakeNullable<T>(int n) where T : class => new(options);
            ApplicationDbContext Prop => new(options);
            async Task<ApplicationDbContext> MakeAsync() => new(options);
            void Outer()
            {
                ApplicationDbContext Local() => new(options);
            }
            """,
            [
                "1 [target-typed-return]", "2 [target-typed-return]", "3 [target-typed-return]",
                "4 [target-typed-return]", "7 [target-typed-return]",
            ]
        },
        {
            "target-typed-return-block",
            """
            ApplicationDbContext Make(bool fresh)
            {
                if (fresh)
                    return new(options);
                return new(other);
            }
            async Task<ApplicationDbContext> MakeAsync()
            {
                await Task.Yield();
                return new(options);
            }
            async ValueTask<ApplicationDbContext?> MakeValueAsync() { return new(options); }
            ApplicationDbContext Prop { get { return new(options); } }
            """,
            [
                "4 [target-typed-return]", "5 [target-typed-return]", "10 [target-typed-return]",
                "12 [target-typed-return]", "13 [target-typed-return]",
            ]
        },
        {
            "s0-target-typed-return",
            """
            internal static class NcScratchContextMaker
            {
                internal static ApplicationDbContext Make(DbContextOptions<ApplicationDbContext> o) => new(o);
            }
            """,
            ["3 [options-type]", "3 [target-typed-return]"]
        },
        {
            "typed-lambda",
            """
            var make = ApplicationDbContext (DbContextOptions<ApplicationDbContext> o) => new(o);
            Func<DbContextOptions<ApplicationDbContext>, ApplicationDbContext> f = o => new(o);
            Func<ApplicationDbContext> g = () =>
            {
                return new(options);
            };
            """,
            ["1 [options-type]", "1 [typed-lambda]", "2 [options-type]", "2 [typed-lambda]", "5 [typed-lambda]"]
        },
        {
            "activation",
            """
            var a = ActivatorUtilities.CreateInstance<ApplicationDbContext>(sp, options);
            var b = ActivatorUtilities.GetServiceOrCreateInstance<Data.ApplicationDbContext>(sp);
            var c = (ApplicationDbContext)Activator.CreateInstance(typeof(ApplicationDbContext), options)!;
            var asm = typeof(ApplicationDbContext).Assembly;
            """,
            ["1 [activation]", "2 [activation]", "3 [activation]"]
        },
        {
            "subclass",
            """
            public class SneakyContext : ApplicationDbContext
            {
                public SneakyContext(DbContextOptions<ApplicationDbContext> o) : base(o) { }
            }
            public sealed class PrimaryContext(DbContextOptions<ApplicationDbContext> o) : ApplicationDbContext(o);
            public record RecordContext : Data.ApplicationDbContext;
            """,
            ["1 [subclass]", "3 [options-type]", "5 [options-type]", "5 [subclass]", "6 [subclass]"]
        },
        {
            "options-type",
            """
            void Seed(ApplicationDbContext db) { }
            void Run(DbContextOptions<ApplicationDbContext> opts) => Seed(new(opts));
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            """,
            ["2 [options-type]", "3 [options-type]"]
        },
        {
            "service-mixed",
            """
            public class SneakyService(DbContextOptions<ApplicationDbContext> options)
            {
                public async Task<int> CountAsync()
                {
                    await using var db = new ApplicationDbContext(options);
                    ApplicationDbContext other = new(options);
                    // new ApplicationDbContext(options) in a comment is not code
                    /// <c>new ApplicationDbContext(</c> in a doc comment is not code either
                    return await db.Rooms.CountAsync();
                }
            }
            """,
            ["1 [options-type]", "5 [explicit-new]", "6 [target-typed-declaration]"]
        },
    };

    [Theory]
    [MemberData(nameof(ConstructionForms))]
    public void NC_fact3_each_construction_form_is_flagged_by_its_rule(string form, string source, string[] expected)
    {
        ContextConstructions(source).Select(h => $"{h.Line} [{h.Rule}]")
            .Should().BeEquivalentTo(expected, "form {0} must yield exactly its (line, rule) set", form);
    }

    [Fact]
    public void NC_fact3_forms_in_comments_and_strings_are_not_code()
    {
        const string notCode = """"
            // new ApplicationDbContext(o); ApplicationDbContext a = new(o); ApplicationDbContext M() => new(o);
            /* ApplicationDbContext (o) => new(o); typeof(ApplicationDbContext); class X : ApplicationDbContext
               DbContextOptions<ApplicationDbContext> Func<int, ApplicationDbContext> f = o => new(o); */
            /// <c>ActivatorUtilities.CreateInstance<ApplicationDbContext>(sp)</c> ApplicationDbContext M() { return new(o); }
            var a = "new ApplicationDbContext(o); \"ApplicationDbContext b = new(o);\" DbContextOptions<ApplicationDbContext>";
            var b = @"ApplicationDbContext M() => new(o);
                class X : ApplicationDbContext { } ""typeof(ApplicationDbContext)"" ";
            var c = $"{n} ApplicationDbContext M() {{ return new(o); }} Func<int, ApplicationDbContext> f = o => new(o);";
            var d = """
                ApplicationDbContext (o) => new(o); GetServiceOrCreateInstance<ApplicationDbContext>(sp)
                record R(DbContextOptions<ApplicationDbContext> o) : ApplicationDbContext(o);
                """;
            var e = '"';
            var asm = typeof(ApplicationDbContext).Assembly;
            """";
        ContextConstructions(notCode).Should().BeEmpty(
            "comments, string and char literals, and typeof(ApplicationDbContext).Assembly are not constructions");
    }

    // Companion to fact 3: the Unfiltered tenant is reachable only through the
    // options-only constructor. The definition in Tenancy/TenantContext.cs is not a call.
    internal static readonly string[] CreateUnfilteredAllowedFiles =
    [
        "Data/ApplicationDbContext.cs",
        "Tenancy/TenantContext.cs", // the definition
    ];

    [Fact]
    public void CreateUnfiltered_is_called_only_by_the_options_only_context_constructor()
    {
        var offenders = OffendersOutside(CreateUnfilteredCallRe, CreateUnfilteredAllowedFiles);

        offenders.Should().BeEmpty(
            "only ApplicationDbContext's options-only constructor may create an Unfiltered tenant (D12). Offenders:\n  " +
            string.Join("\n  ", offenders));

        var context = File.ReadAllText(Path.Combine(AppSourceRoot(), "Data", "ApplicationDbContext.cs"));
        CodeLinesMatching(context, CreateUnfilteredCallRe).Should().ContainSingle(
            "guard the guard: the one sanctioned call is seen");
    }

    [Fact]
    public void NC_a_CreateUnfiltered_call_is_flagged()
    {
        const string service = """
            var tenant = TenantContext.CreateUnfiltered();
            // TenantContext.CreateUnfiltered() in a comment is not code
            """;
        CodeLinesMatching(service, CreateUnfilteredCallRe).Should().Equal([1]);
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
