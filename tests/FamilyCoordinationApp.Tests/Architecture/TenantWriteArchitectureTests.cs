using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// The write-side guard facts (fca-household-scope WP-03, D9). Both scan every <c>.cs</c>/<c>.cshtml</c> file under
/// <c>src/FamilyCoordinationApp</c> (Migrations, bin and obj excluded) after
/// <see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/> has blanked comments and string literals, so a
/// doc comment or a message may name either form.
/// <list type="bullet">
/// <item><b>The cross-write allowlist.</b> Calls of <c>.AllowCrossTenantWrite(</c> per file equal exactly
/// <see cref="AllowCrossTenantWriteCallsPerFile"/>: the invite accept, once. A declaration has no <c>.</c> receiver, so
/// the definitions in <c>Tenancy/</c> never count, and nothing is excluded. Stated limit, as with <c>RunAs</c>'s map:
/// a same-count substitution inside the allowlisted file passes.</item>
/// <item><b>No bulk reassignment.</b> <c>ExecuteUpdate</c> bypasses <c>SaveChanges</c>, and the Tenant filter scopes
/// which rows it selects, not what it assigns. So no <c>SetProperty(</c> whose first argument (the property selector)
/// names a <c>HouseholdId</c>-family column. The selector is read from the original text, strings included, so
/// <c>EF.Property&lt;int&gt;(u, "HouseholdId")</c> counts. Stated limits: a selector held in a variable, or a comma
/// inside generic type arguments of the selector (which ends it early), is not seen.</item>
/// </list>
/// Each fact has an in-tree negative control that feeds its detector known-bad input.
/// </summary>
public class TenantWriteArchitectureTests
{
    internal static readonly Dictionary<string, int> AllowCrossTenantWriteCallsPerFile = new(StringComparer.Ordinal)
    {
        ["Services/HouseholdConnectionService.cs"] = 1, // invite accept marks the inviting household's invite used (D9)
    };

    private static readonly Regex AllowCrossTenantWriteCallRe = new(@"\.\s*AllowCrossTenantWrite\s*\(");
    private static readonly Regex SetPropertyCallRe = new(@"\bSetProperty\s*(?:<[^()]*>)?\s*\(");
    private static readonly Regex HouseholdIdRe = new(@"HouseholdId\w*");

    internal static List<int> AllowCrossTenantWriteCallLines(string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        return AllowCrossTenantWriteCallRe.Matches(code).Select(m => LineOf(code, m.Index)).ToList();
    }

    /// <summary>The lines of <c>SetProperty(</c> calls whose property selector names a <c>HouseholdId</c> column.</summary>
    internal static List<int> SetPropertyOnHouseholdIdLines(string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        var lines = new List<int>();
        foreach (Match m in SetPropertyCallRe.Matches(code))
        {
            var start = m.Index + m.Length;
            var end = FirstArgumentEnd(code, start);
            // Stripping keeps offsets, so the selector is read back from the original, string literals included.
            if (HouseholdIdRe.IsMatch(source[start..end])) lines.Add(LineOf(code, m.Index));
        }
        return lines;
    }

    /// <summary>The offset of the comma (or closing parenthesis) that ends the argument starting at <paramref name="start"/>.</summary>
    private static int FirstArgumentEnd(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth == 0: return i;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: return i;
            }
        }
        return code.Length;
    }

    private static int LineOf(string code, int index) => code[..index].Count(c => c == '\n') + 1;

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

    [Fact]
    public void AllowCrossTenantWrite_is_called_only_from_the_allowlisted_files_with_the_allowlisted_counts()
    {
        var calls = AppSources()
            .Select(f => (f.RelativePath, Count: AllowCrossTenantWriteCallLines(f.Source).Count))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.RelativePath, f => f.Count, StringComparer.Ordinal);

        calls.Should().BeEquivalentTo(AllowCrossTenantWriteCallsPerFile,
            "AllowCrossTenantWrite opts a save out of the write step's household check, so its call sites are pinned: " +
            "the invite accept only (D9; E7 caps it at 2). In src: " +
            string.Join(", ", calls.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")));
    }

    [Fact]
    public void No_SetProperty_assigns_a_HouseholdId_in_src()
    {
        var offenders = AppSources()
            .SelectMany(f => SetPropertyOnHouseholdIdLines(f.Source).Select(line => $"{f.RelativePath}:{line}"))
            .ToList();

        offenders.Should().BeEmpty(
            "ExecuteUpdate bypasses SaveChanges: the Tenant filter limits which rows it selects, not what it assigns, so " +
            "a SetProperty on HouseholdId would move rows to another household (D9). Offenders:\n" +
            string.Join("\n", offenders));
    }

    // ── In-tree negative controls: each detector fails on known-bad input ────

    [Fact]
    public void NC_an_AllowCrossTenantWrite_call_is_seen_and_a_declaration_or_mention_is_not()
    {
        const string source = """
            using (context.Tenant.AllowCrossTenantWrite("reason")) { }
            var a = tenant
                .AllowCrossTenantWrite("split across lines");
            // context.Tenant.AllowCrossTenantWrite("in a comment");
            var s = "context.Tenant.AllowCrossTenantWrite(\"in a string\")";
            public IDisposable AllowCrossTenantWrite(string reason) => null;
            using (t.AllowCrossTenantWrite("one")) using (t.AllowCrossTenantWrite("two")) { }
            """;

        AllowCrossTenantWriteCallLines(source).Should().Equal(1, 3, 7, 7); // a split call reports its `.AllowCrossTenantWrite` line
    }

    [Fact]
    public void NC_a_SetProperty_on_HouseholdId_is_seen_and_other_SetProperty_calls_are_not()
    {
        const string source = """
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.HouseholdId, 2));
            await db.Rooms.ExecuteUpdateAsync(s => s
                .SetProperty(
                    r => r.HouseholdId,
                    r => r.HouseholdId + 1));
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => EF.Property<int>(u, "HouseholdId"), 2));
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty<int>(u => u.HouseholdId, 2));
            await db.HouseholdConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.HouseholdId1, 3));
            await db.ChoreDigestSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSentAt, now));
            await db.Chores.ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, c => c.HouseholdId == 1 ? "a" : "b"));
            // await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.HouseholdId, 2));
            var text = "s.SetProperty(u => u.HouseholdId, 2)";
            """;

        SetPropertyOnHouseholdIdLines(source).Should().Equal(1, 3, 6, 7, 8);
    }
}
