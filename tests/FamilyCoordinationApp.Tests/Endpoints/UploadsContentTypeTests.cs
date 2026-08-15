using FluentAssertions;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Endpoints;

/// <summary>
/// Locks the two halves of the upload contract together (A5). <see cref="ImageService"/> decides what may be
/// WRITTEN; <see cref="UploadsEndpoints"/> decides what may be SERVED — and since the read path now 404s any
/// extension it has no media type for, a type accepted on upload but missing from the serve map would produce
/// files that store successfully and can never be displayed. Before the gate this could not happen: the
/// static-file middleware served whatever was on disk.
/// </summary>
public class UploadsContentTypeTests
{
    [Fact]
    public void EveryUploadableExtension_IsServable()
    {
        var missing = ImageService.AllowedExtensions
            .Where(ext => !UploadsEndpoints.ContentTypes.ContainsKey(ext))
            .ToList();

        missing.Should().BeEmpty(
            "every extension ImageService accepts on upload must have a media type in UploadsEndpoints.ContentTypes, "
            + "or files of that type upload fine and then 404 on read");
    }

    [Fact]
    public void EveryServableExtension_IsUploadable()
    {
        // The other direction: a media type for something the app will never store is dead configuration,
        // and — worse — widens the set of on-disk files the gate is willing to hand out.
        var extra = UploadsEndpoints.ContentTypes.Keys
            .Where(ext => !ImageService.AllowedExtensions.Contains(ext))
            .ToList();

        extra.Should().BeEmpty();
    }

    [Fact]
    public void ExtensionMatching_IsCaseInsensitive()
    {
        // Path.GetExtension preserves the caller's casing, and ".JPG" is a perfectly normal upload.
        UploadsEndpoints.ContentTypes.ContainsKey(".JPG").Should().BeTrue();
        UploadsEndpoints.ContentTypes.ContainsKey(".PnG").Should().BeTrue();
    }
}
