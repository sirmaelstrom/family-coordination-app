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
/// </summary>
public sealed class FieldLengthArchitectureTests
{
    private static readonly Regex LimitCall = new(@"\b(HasMaxLength|MaxTextLength)\s*\(\s*(?!FieldLengths\.)", RegexOptions.Compiled);
    private static readonly Regex AnyLimitCall = new(@"\b(HasMaxLength|MaxTextLength)\s*\(", RegexOptions.Compiled);

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
    /// The services keep three limits of their own (their files are outside the validation change's boundary). Pin
    /// them to the column constant so a change to either side fails here instead of in production.
    /// </summary>
    [Fact]
    public void Service_side_limits_match_their_columns()
    {
        using (new AssertionScope())
        {
            FeedbackService.MessageMaxLength.Should().Be(FieldLengths.Feedback.Message);
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
