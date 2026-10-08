using System.Reflection;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// One source per column limit (quest ec7a7331). The EF configurations declare every string column's length from
/// <see cref="FieldLengths"/>, and the <c>/api</c> request records validate against the same constants, so a limit
/// cannot drift between the column and the check. The scan reads the comment- and string-stripped <c>src</c>
/// (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>), so a doc mention is not a use.
/// <para><b>Key:</b> every <c>HasMaxLength(</c> / <c>MaxTextLength(</c> call whose argument does not start with
/// <c>FieldLengths.</c>, reported as file:line. <b>Stated limit:</b> an argument that names a <c>FieldLengths</c>
/// constant and then does arithmetic on it (<c>FieldLengths.X.Y + 1</c>) passes; none exist:
/// <c>grep -rnE "(HasMaxLength|MaxTextLength)\(FieldLengths\.[A-Za-z.]+ *[-+*/]" src</c> returns nothing.</para>
/// <para><b>Column coverage</b> (<see cref="Every_FieldLengths_constant_declares_exactly_one_column"/>): the
/// <c>HasMaxLength(FieldLengths.X.Y)</c> calls in <c>Data/Configurations</c> name each <see cref="FieldLengths"/>
/// constant exactly once, so a removed call (its constant now names no column) or a duplicated one fails. EF does not
/// catch a removed call either: <c>PendingModelChangesWarning</c> is suppressed (<c>Program.cs</c>, the
/// <c>ConfigureWarnings</c> call), so the startup migrator does not refuse a model that drifted from the snapshot.</para>
/// <para><b>Not covered:</b> a call that names the WRONG constant (<c>Room.Name</c> on the chore name column) passes
/// both facts; a request record missing its attribute is not detected (no rule says which fields must carry one);
/// and a limit declared some other way (<c>[StringLength]</c>/<c>[MaxLength]</c> on an entity,
/// <c>HasColumnType("varchar(n)")</c>) is invisible to the scan.</para>
/// </summary>
public sealed class FieldLengthArchitectureTests
{
    private static readonly Regex LimitCall = new(@"\b(HasMaxLength|MaxTextLength)\s*\(\s*(?!FieldLengths\.)", RegexOptions.Compiled);
    private static readonly Regex AnyLimitCall = new(@"\b(HasMaxLength|MaxTextLength)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ColumnLimitCall = new(@"\bHasMaxLength\s*\(\s*FieldLengths\.(\w+)\.(\w+)", RegexOptions.Compiled);

    /// <summary>Every <c>FieldLengths.Entity.Field</c> constant, by name.</summary>
    internal static List<string> FieldLengthConstants() =>
        typeof(FieldLengths).GetNestedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => $"{t.Name}.{f.Name}"))
            .ToList();

    [Fact]
    public void Every_FieldLengths_constant_declares_exactly_one_column()
    {
        var used = TenantScopeArchitectureTests.AppSources()
            .Where(f => f.RelativePath.StartsWith("Data/Configurations/", StringComparison.Ordinal))
            .SelectMany(f => ColumnLimitNames(f.Source))
            .ToList();
        var constants = FieldLengthConstants();

        using (new AssertionScope())
        {
            constants.Should().HaveCount(57, "FieldLengths holds the 57 column limits (re-count it if a column is added)");
            used.Should().BeEquivalentTo(constants,
                "each column constant must be declared by exactly one HasMaxLength call in Data/Configurations; a " +
                "missing name means its column lost its limit, a repeated one means two columns share a constant");
        }
    }

    private static IEnumerable<string> ColumnLimitNames(string source) =>
        ColumnLimitCall.Matches(TenantScopeArchitectureTests.StripCommentsAndStrings(source))
            .Select(m => $"{m.Groups[1].Value}.{m.Groups[2].Value}");

    [Fact]
    public void Every_column_limit_and_request_limit_reads_FieldLengths()
    {
        var sources = TenantScopeArchitectureTests.AppSources().ToList();
        var calls = sources.Sum(f => AnyLimitCall.Matches(TenantScopeArchitectureTests.StripCommentsAndStrings(f.Source)).Count);
        var literal = sources.SelectMany(f => LiteralLimits(f.RelativePath, f.Source)).ToList();

        using (new AssertionScope())
        {
            calls.Should().BeGreaterThan(57, "the scanner must see the 57 HasMaxLength calls plus the request attributes (guard the guard)");
            literal.Should().BeEmpty(
                "a column or request limit must name its FieldLengths constant, so the column and the check share one " +
                "source (quest ec7a7331). Literal:\n{0}", string.Join("\n", literal));
        }
    }

    /// <summary>
    /// The services keep four limits of their own (their files are outside the validation change's boundary). Pin
    /// them to the column constant so a change to either side fails here instead of in production.
    /// </summary>
    [Fact]
    public void Service_side_limits_match_their_columns()
    {
        using (new AssertionScope())
        {
            FeedbackService.MessageMaxLength.Should().Be(FieldLengths.Feedback.Message);

            // FeedbackService truncates the diagnostic fields to this before storing them.
            var diagnosticMax = PrivateConst(typeof(FeedbackService), "DiagnosticMaxLength");
            diagnosticMax.Should().Be(FieldLengths.Feedback.CurrentPage);
            diagnosticMax.Should().Be(FieldLengths.Feedback.UserAgent);
            PrivateConst(typeof(ChoreSubtaskService), "MaxTitleLength").Should().Be(FieldLengths.ChoreSubtask.Title);

            var imagePathMax = PrivateConst(typeof(ImagePathPolicy), "MaxLength");
            imagePathMax.Should().Be(FieldLengths.Recipe.ImagePath);
            imagePathMax.Should().Be(FieldLengths.Chore.PhotoPath);
            imagePathMax.Should().Be(FieldLengths.Room.PhotoPath);
            imagePathMax.Should().Be(FieldLengths.ChoreCompletion.PhotoPath);
        }
    }

    // ── Negative controls: feed the detector known input ───────────────────────────────

    [Fact]
    public void Detector_flags_a_literal_column_limit()
    {
        LiteralLimits("Data/Configurations/X.cs", "builder.Property(c => c.Name)\n    .HasMaxLength(50);")
            .Should().ContainSingle().Which.Should().Be("Data/Configurations/X.cs:2");
    }

    [Fact]
    public void Detector_flags_a_literal_request_limit_and_passes_a_named_one()
    {
        const string source = """
            public sealed record A([MaxTextLength(200)] string Name);
            public sealed record B([MaxTextLength(FieldLengths.Room.Name)] string Name);
            // a comment naming HasMaxLength(7) is not a use
            """;

        LiteralLimits("Endpoints/X.cs", source).Should().Equal("Endpoints/X.cs:1");
    }

    private static IEnumerable<string> LiteralLimits(string relativePath, string source)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        foreach (Match m in LimitCall.Matches(code))
        {
            var line = code[..m.Index].Count(c => c == '\n') + 1;
            yield return $"{relativePath}:{line}";
        }
    }

    private static int PrivateConst(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull($"{type.Name}.{name} is the limit this test pins; if it moved, repoint the test");
        return (int)field!.GetRawConstantValue()!;
    }
}
