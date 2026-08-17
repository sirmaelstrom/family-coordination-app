using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Security;

/// <summary>
/// Security tests for XSS prevention, URL validation, and path traversal protection.
/// These tests validate that security-critical code behaves correctly.
/// </summary>
public class SecurityTests
{
    #region MarkdownHelper XSS Prevention Tests

    [Fact]
    public void ToSafeHtml_ScriptTag_IsSanitized()
    {
        // Arrange
        var malicious = "<script>alert('xss')</script>";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert
        result.Should().NotContain("<script>");
        result.Should().NotContain("</script>");
    }

    [Fact]
    public void ToSafeHtml_ImgOnerror_IsSanitized()
    {
        // Arrange
        var malicious = "<img src=\"x\" onerror=\"alert('xss')\">";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert - onerror attribute should be stripped by sanitizer
        // The img tag may still exist (it's allowed) but without event handlers
        result.Should().NotContain("onerror=");
        result.Should().NotContain("alert(");
    }

    [Fact]
    public void ToSafeHtml_JavascriptUrl_IsSanitized()
    {
        // Arrange
        var malicious = "[click me](javascript:alert('xss'))";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert
        result.Should().NotContain("javascript:");
    }

    [Fact]
    public void ToSafeHtml_ValidMarkdown_IsPreserved()
    {
        // Arrange
        var valid = "**bold** and *italic*";

        // Act
        var result = MarkdownHelper.ToSafeHtml(valid);

        // Assert
        result.Should().Contain("<strong>bold</strong>");
        result.Should().Contain("<em>italic</em>");
    }

    [Fact]
    public void ToSafeHtml_IframeTag_IsSanitized()
    {
        // Arrange
        var malicious = "<iframe src='https://evil.com'></iframe>";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert
        result.Should().NotContain("<iframe");
        result.Should().NotContain("</iframe>");
    }

    [Fact]
    public void ToSafeHtml_OnclickAttribute_IsSanitized()
    {
        // Arrange
        var malicious = "<div onclick='alert(1)'>Click me</div>";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert
        result.Should().NotContain("onclick");
    }

    [Fact]
    public void ToSafeHtml_DataUrl_IsSanitized()
    {
        // Arrange
        var malicious = "<a href='data:text/html,<script>alert(1)</script>'>click</a>";

        // Act
        var result = MarkdownHelper.ToSafeHtml(malicious);

        // Assert
        result.Should().NotContain("data:");
    }

    [Fact]
    public void ToSafeHtml_NullInput_ReturnsEmpty()
    {
        // Act
        var result = MarkdownHelper.ToSafeHtml(null);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToSafeHtml_EmptyInput_ReturnsEmpty()
    {
        // Act
        var result = MarkdownHelper.ToSafeHtml("");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToSafeHtml_WhitespaceInput_ReturnsEmpty()
    {
        // Act
        var result = MarkdownHelper.ToSafeHtml("   ");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToSafeHtml_SafeLink_IsPreserved()
    {
        // Arrange
        var valid = "[Example](https://example.com)";

        // Act
        var result = MarkdownHelper.ToSafeHtml(valid);

        // Assert
        result.Should().Contain("href=\"https://example.com\"");
        result.Should().Contain(">Example</a>");
    }

    [Fact]
    public void ToSafeHtml_SafeImage_IsPreserved()
    {
        // Arrange
        var valid = "![alt text](https://example.com/image.jpg)";

        // Act
        var result = MarkdownHelper.ToSafeHtml(valid);

        // Assert
        result.Should().Contain("<img");
        result.Should().Contain("src=\"https://example.com/image.jpg\"");
        result.Should().Contain("alt=\"alt text\"");
    }

    #endregion

    #region UrlValidator Tests

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://www.google.com")]
    [InlineData("https://example.com/path/to/page")]
    public void ValidateUrl_ValidExternalUrl_ReturnsValid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:void(0)")]
    [InlineData("vbscript:msgbox('xss')")]
    public void ValidateUrl_JavascriptScheme_ReturnsInvalid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("not allowed");
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void ValidateUrl_NonHttpScheme_ReturnsInvalid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("not allowed");
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://127.0.0.1/admin")]
    public void ValidateUrl_LoopbackAddress_ReturnsInvalid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("internal networks");
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://localhost/api")]
    public void ValidateUrl_Localhost_ReturnsInvalid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("internal networks");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateUrl_EmptyOrNull_ReturnsInvalid(string? url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url!);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("empty");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("://missing-scheme.com")]
    [InlineData("http://")]
    public void ValidateUrl_InvalidFormat_ReturnsInvalid(string url)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var (isValid, errorMessage) = validator.ValidateUrl(url);

        // Assert
        isValid.Should().BeFalse();
        errorMessage.Should().Contain("Invalid URL");
    }

    #endregion

    #region IsUrlSafe Tests (wrapper method)

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("", false)]
    public void IsUrlSafe_ReturnsExpectedResult(string url, bool expected)
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var result = validator.IsUrlSafe(url);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void IsUrlSafe_NullUrl_ReturnsFalse()
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var result = validator.IsUrlSafe(null!);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafe_LoopbackAddress_ReturnsFalse()
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var result = validator.IsUrlSafe("http://127.0.0.1");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafe_Localhost_ReturnsFalse()
    {
        // Arrange
        var validator = new UrlValidator();

        // Act
        var result = validator.IsUrlSafe("http://localhost");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Path Traversal + Tenant Scoping (ImageService.DeleteImageAsync)

    // These run against a REAL temporary wwwroot and assert on THE FILE. The previous versions asserted only
    // "does not throw" — which every input satisfies whether it was blocked or deleted, so they could not
    // distinguish the two; and with the mocked "/var/www/app/wwwroot" root, Path.GetFullPath on Windows
    // rebased every path to C:\var\www\… so even the "valid path is allowed" case was silently taking the
    // blocked branch. Observing the file is what makes the boundary testable at all.

    [Fact]
    public async Task DeleteImageAsync_OwnHouseholdPath_DeletesTheFile()
    {
        using var root = new TempWebRoot();
        var file = root.WriteUpload(householdId: 1, fileName: "photo.jpg");

        await NewImageService(root.Path).DeleteImageAsync("/uploads/1/photo.jpg", householdId: 1);

        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteImageAsync_OtherHouseholdPath_LeavesFileIntact()
    {
        // The A5 tenant boundary. This path is traversal-free and inside WebRootPath, so the old
        // WebRootPath-only guard accepted it and the file WAS deleted.
        using var root = new TempWebRoot();
        var victim = root.WriteUpload(householdId: 2, fileName: "photo.jpg");

        await NewImageService(root.Path).DeleteImageAsync("/uploads/2/photo.jpg", householdId: 1);

        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteImageAsync_SiblingHouseholdWithPrefixedId_LeavesFileIntact()
    {
        // "uploads/12" starts with "uploads/1", so a StartsWith check without a trailing separator treats
        // household 12's directory as being inside household 1's.
        using var root = new TempWebRoot();
        var victim = root.WriteUpload(householdId: 12, fileName: "photo.jpg");

        await NewImageService(root.Path).DeleteImageAsync("/uploads/12/photo.jpg", householdId: 1);

        File.Exists(victim).Should().BeTrue();
    }

    [Theory]
    [InlineData("/../outside.jpg")]
    [InlineData("/uploads/../outside.jpg")]
    [InlineData("/uploads/1/../../outside.jpg")]
    public async Task DeleteImageAsync_PathTraversal_LeavesFileIntact(string maliciousPath)
    {
        using var root = new TempWebRoot();
        // The traversal target: a real file the escape would reach if the guard let it through.
        var outside = Path.Combine(root.Path, "outside.jpg");
        File.WriteAllText(outside, "x");

        await NewImageService(root.Path).DeleteImageAsync(maliciousPath, householdId: 1);

        File.Exists(outside).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteImageAsync_BlankPath_HandledGracefully(string? blankPath)
    {
        using var root = new TempWebRoot();

        await NewImageService(root.Path).Invoking(s => s.DeleteImageAsync(blankPath!, householdId: 1))
            .Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("https://evil.example/x.jpg")]  // PhotoPath is unvalidated client input on chore/room write
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("/uploads/1/\u0000name.jpg")]
    [InlineData("//////")]
    public async Task DeleteImageAsync_MalformedPath_DoesNotThrow(string malformedPath)
    {
        // These reach DeleteImageAsync directly: chore/room PhotoPath is stored verbatim from the request
        // body and replayed here by delete-on-replace. A path the OS refuses to parse must be refused, not
        // surfaced as a 500 on an otherwise-valid update.
        using var root = new TempWebRoot();

        await NewImageService(root.Path).Invoking(s => s.DeleteImageAsync(malformedPath, householdId: 1))
            .Should().NotThrowAsync();
    }

    #endregion

    #region Cross-household image COPY (ImageService.CopyImageAsync — quest b0edfd94)

    [Fact]
    public async Task CopyImage_DuplicatesTheFileIntoTheTargetHousehold()
    {
        using var root = new TempWebRoot();
        var sourceDir = Directory.CreateDirectory(Path.Combine(root.Path, "uploads", "1"));
        var sourceFile = Path.Combine(sourceDir.FullName, "photo.jpg");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3 });

        var newPath = await NewImageService(root.Path).CopyImageAsync("/uploads/1/photo.jpg", 1, 2);

        newPath.Should().StartWith("/uploads/2/").And.EndWith(".jpg");
        var newFile = Path.Combine(root.Path, "uploads", "2", Path.GetFileName(newPath!));
        File.Exists(newFile).Should().BeTrue();
        (await File.ReadAllBytesAsync(newFile)).Should().Equal(1, 2, 3);
        File.Exists(sourceFile).Should().BeTrue("copy, not move — the source household keeps its file");
    }

    [Theory]
    [InlineData("/uploads/2/photo.jpg")] // not the claimed source household's path
    [InlineData("/uploads/1/../2/photo.jpg")] // traversal out of the source directory
    [InlineData("https://example.test/photo.jpg")] // external URL — not a stored upload
    [InlineData("")]
    public async Task CopyImage_RefusesNonOwnedSources(string sourcePath)
    {
        using var root = new TempWebRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "uploads", "2"));
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "uploads", "2", "photo.jpg"), new byte[] { 9 });

        var result = await NewImageService(root.Path).CopyImageAsync(sourcePath, sourceHouseholdId: 1, targetHouseholdId: 3);

        result.Should().BeNull();
        Directory.Exists(Path.Combine(root.Path, "uploads", "3")).Should().BeFalse("nothing may be created for a refused copy");
    }

    [Fact]
    public async Task CopyImage_MissingSourceFile_ReturnsNull()
    {
        using var root = new TempWebRoot();

        var result = await NewImageService(root.Path).CopyImageAsync("/uploads/1/gone.jpg", 1, 2);

        result.Should().BeNull();
    }

    #endregion

    private static ImageService NewImageService(string webRootPath)
    {
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns(webRootPath);
        return new ImageService(mockEnv.Object, new Mock<ILogger<ImageService>>().Object);
    }

    /// <summary>A throwaway wwwroot on disk, so a delete either happens or does not and the test can see which.</summary>
    private sealed class TempWebRoot : IDisposable
    {
        public string Path { get; }

        public TempWebRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fca-imagesvc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string WriteUpload(int householdId, string fileName)
        {
            var dir = System.IO.Path.Combine(Path, "uploads", householdId.ToString());
            Directory.CreateDirectory(dir);
            var fullPath = System.IO.Path.Combine(dir, fileName);
            File.WriteAllText(fullPath, "x");
            return fullPath;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
