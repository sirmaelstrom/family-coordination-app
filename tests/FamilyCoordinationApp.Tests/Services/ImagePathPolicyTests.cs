using FluentAssertions;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The write-boundary policy for ImagePath/PhotoPath (quest b0edfd94). Three accepted shapes —
/// nothing, absolute http(s), own-household upload — and everything else refused, most importantly a
/// path into ANOTHER household's uploads directory (the attacker-authored-predicate hazard the
/// uploads gate documents) and OS-unparseable strings (an embedded NUL used to 500 inside
/// delete-on-replace).
/// </summary>
public class ImagePathPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInput_NormalizesToNull(string? raw)
    {
        ImagePathPolicy.TryNormalize(raw, householdId: 1, out var normalized).Should().BeTrue();
        normalized.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.test/img.jpg")]
    [InlineData("http://cdn.example.test/a/b/c.png?w=800")]
    public void AbsoluteHttpUrl_IsAccepted(string raw)
    {
        ImagePathPolicy.TryNormalize(raw, 1, out var normalized).Should().BeTrue();
        normalized.Should().Be(raw);
    }

    [Fact]
    public void OwnHouseholdUpload_IsAccepted()
    {
        ImagePathPolicy.TryNormalize("/uploads/7/9f8e0d.jpg", householdId: 7, out var normalized)
            .Should().BeTrue();
        normalized.Should().Be("/uploads/7/9f8e0d.jpg");
    }

    [Fact]
    public void AnotherHouseholdsUpload_IsRefused()
    {
        // The core rule: a stored row must never point into a directory the writer does not own —
        // any later feature keying on such a row would be an attacker-authored predicate.
        ImagePathPolicy.TryNormalize("/uploads/2/theirs.jpg", householdId: 1, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("/uploads/1/../2/theirs.jpg")]
    [InlineData("/uploads/1/sub/dir.jpg")]
    [InlineData("/uploads/1/..\\x.jpg")]
    [InlineData("/uploads/1/")]
    [InlineData("/uploads/12/x.jpg")] // prefix trap: household 1 writing into household 12
    [InlineData("uploads/1/x.jpg")] // relative
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x.jpg")]
    public void EverythingElse_IsRefused(string raw)
    {
        ImagePathPolicy.TryNormalize(raw, householdId: 1, out _).Should().BeFalse();
    }

    [Fact]
    public void EmbeddedNul_IsRefused()
    {
        ImagePathPolicy.TryNormalize("/uploads/1/a\0b.jpg", 1, out _).Should().BeFalse();
    }

    [Fact]
    public void OverlongValue_IsRefusedNotTruncated()
    {
        var raw = "https://example.test/" + new string('a', 500);
        ImagePathPolicy.TryNormalize(raw, 1, out _).Should().BeFalse();
    }
}
