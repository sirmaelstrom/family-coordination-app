using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FamilyCoordinationApp.Tests.Architecture;

/// <summary>
/// The structural half of the central exception mapping (quest 940b4022) and the one concurrency path (quest 8cf3c806).
/// The HTTP behavior is proven by <c>Integration/ApiExceptionMappingTests</c>; these facts keep the shape from
/// regrowing: endpoints don't map exceptions, domain exceptions never inherit a BCL catch, every row-versioned entity
/// is <see cref="IVersioned"/>, and a <c>DbUpdateConcurrencyException</c> is caught in exactly one file.
/// <para><b>Key:</b> the source scans read the comment- and string-stripped <c>src</c>
/// (<see cref="TenantScopeArchitectureTests.StripCommentsAndStrings"/>) and identify a site by file and trimmed line
/// text, so a moved line keeps its key and a doc mention is not a site.</para>
/// </summary>
public sealed class ExceptionMappingArchitectureTests
{
    private static readonly Regex CatchClause = new(@"\bcatch\s*\(", RegexOptions.Compiled);
    private static readonly Regex ConcurrencyCatch = new(@"\bcatch\s*\(\s*(?:Microsoft\.EntityFrameworkCore\.)?DbUpdateConcurrencyException\b", RegexOptions.Compiled);

    /// <summary>
    /// The one endpoint catch left: a BCL path-parse guard that answers the capability 404, not a domain mapping.
    /// </summary>
    internal static readonly (string File, string Line)[] EndpointCatchAllowlist =
    [
        ("Endpoints/UploadsEndpoints.cs", "catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)"),
    ];

    /// <summary>
    /// The one <c>DbUpdateConcurrencyException</c> handler: the one save path. The shopping item's merge-retry was the
    /// second until its PATCH gained a client token (quest 8cf3c806, operator answer c).
    /// </summary>
    internal static readonly string[] ConcurrencyCatchFiles = ["Data/ConcurrencySave.cs"];

    [Fact]
    public void DomainException_derives_from_Exception_so_no_BCL_catch_can_see_it()
    {
        typeof(DomainException).BaseType.Should().Be(typeof(Exception),
            "InvalidOperationException/ArgumentException/KeyNotFoundException are what EF and the framework throw for their " +
            "own faults; a domain type deriving from one inherits every catch already written for it");

        var domainTypes = typeof(DomainException).Assembly.GetTypes()
            .Where(t => typeof(DomainException).IsAssignableFrom(t))
            .ToList();
        domainTypes.Should().Contain([typeof(NotFoundException), typeof(DomainValidationException), typeof(ConflictException),
            typeof(ConcurrencyConflictException), typeof(ForbiddenException), typeof(ChoreNotFoundException),
            typeof(ChoreValidationException), typeof(DigestSettingsValidationException)]);
        domainTypes.Where(BclBased).Should().BeEmpty();
    }

    [Fact]
    public void Endpoints_catch_nothing_the_exception_branch_maps()
    {
        var sites = TenantScopeArchitectureTests.AppSources()
            .Where(f => f.RelativePath.StartsWith("Endpoints/", StringComparison.Ordinal))
            .SelectMany(f => Sites(f.RelativePath, f.Source, CatchClause))
            .ToList();

        var (unlisted, stale) = Compare(sites, EndpointCatchAllowlist);

        using (new AssertionScope())
        {
            unlisted.Should().BeEmpty(
                "a service throws a DomainException and ApiExceptionResponse answers its status; an endpoint catch re-grows " +
                "the per-endpoint map (and a catch of a BCL type hides server faults as 4xx). Unlisted:\n{0}",
                string.Join("\n", unlisted));
            stale.Should().BeEmpty("every allowlist entry must name a catch that still exists. Stale:\n{0}", string.Join("\n", stale));
        }
    }

    [Fact]
    public void DbUpdateConcurrencyException_is_caught_only_by_the_one_save_path()
    {
        var files = TenantScopeArchitectureTests.AppSources()
            .Where(f => Sites(f.RelativePath, f.Source, ConcurrencyCatch).Count > 0)
            .Select(f => f.RelativePath)
            .ToList();

        files.Should().BeEquivalentTo(ConcurrencyCatchFiles,
            "a lost update surfaces through ConcurrencySave as one ConcurrencyConflictException (409); a new handler is a " +
            "fifth semantic");
    }

    [Fact]
    public void Every_row_versioned_entity_is_IVersioned_and_nothing_else_is()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"versioned-{Guid.NewGuid():N}").Options);

        var rowVersioned = RowVersionedTypes(db.Model);
        var marked = typeof(IVersioned).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IVersioned).IsAssignableFrom(t));

        rowVersioned.Should().HaveCount(6, "the six IsRowVersion entities re-grepped at the start of quest 8cf3c806 (guard the guard)");
        marked.Should().BeEquivalentTo(rowVersioned,
            "an entity with a row version saves through ConcurrencySave, which needs the IVersioned marker");
    }

    // ── Detectors ─────────────────────────────────────────────────────────────────────

    internal static bool BclBased(Type t) =>
        typeof(InvalidOperationException).IsAssignableFrom(t)
        || typeof(ArgumentException).IsAssignableFrom(t)
        || typeof(KeyNotFoundException).IsAssignableFrom(t);

    internal static List<Type> RowVersionedTypes(IModel model) =>
        model.GetEntityTypes()
            .Where(t => t.GetProperties().Any(p => p.IsConcurrencyToken && p.ValueGenerated == ValueGenerated.OnAddOrUpdate))
            .Select(t => t.ClrType)
            .ToList();

    internal static List<(string File, string Line)> Sites(string file, string source, Regex pattern)
    {
        var code = TenantScopeArchitectureTests.StripCommentsAndStrings(source);
        var originalLines = source.Split('\n');
        return pattern.Matches(code)
            .Select(m => code[..m.Index].Count(c => c == '\n'))
            .Select(line => (file, originalLines[line].Trim()))
            .ToList();
    }

    internal static (List<string> Unlisted, List<string> Stale) Compare(
        IReadOnlyCollection<(string File, string Line)> found, IReadOnlyCollection<(string File, string Line)> allow)
    {
        var remaining = allow.ToList();
        var unlisted = new List<string>();
        foreach (var site in found)
        {
            if (!remaining.Remove(site)) unlisted.Add($"{site.File}: {site.Line}");
        }
        return (unlisted, remaining.Select(s => $"{s.File}: {s.Line}").ToList());
    }

    // ── Negative controls: feed the detectors known input ─────────────────────────────

    [Fact]
    public void NC_an_endpoint_catch_is_found_and_a_comment_or_string_mention_is_not()
    {
        const string source = """
            // catch (InvalidOperationException) in a comment
            var label = "catch (KeyNotFoundException)";
            try { await svc.UpdateRoomAsync(); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            """;

        var sites = Sites("Endpoints/X.cs", source, CatchClause);

        sites.Should().Equal(("Endpoints/X.cs", "catch (InvalidOperationException) { return Results.NotFound(); }"));
        Compare(sites, EndpointCatchAllowlist).Unlisted.Should().ContainSingle();
        Compare([], EndpointCatchAllowlist).Stale.Should().ContainSingle();
    }

    [Fact]
    public void NC_a_concurrency_catch_is_found_with_or_without_its_namespace()
    {
        const string source = """
            catch (DbUpdateConcurrencyException ex) { }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) { }
            catch (DbUpdateException) { }
            """;

        Sites("Services/X.cs", source, ConcurrencyCatch).Should().HaveCount(2);
    }

    [Fact]
    public void NC_the_BCL_detector_sees_each_base_and_passes_a_plain_exception()
    {
        BclBased(typeof(ObjectDisposedException)).Should().BeTrue("ObjectDisposedException : InvalidOperationException");
        BclBased(typeof(ArgumentNullException)).Should().BeTrue();
        BclBased(typeof(KeyNotFoundException)).Should().BeTrue();
        BclBased(typeof(DomainException)).Should().BeFalse();
    }
}
