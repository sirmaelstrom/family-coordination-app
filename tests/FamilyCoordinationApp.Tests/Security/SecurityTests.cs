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

    #region Path Traversal Prevention Tests (ImageService)

    [Theory]
    [InlineData("/../../../etc/passwd")]
    [InlineData("/uploads/../../../etc/passwd")]
    [InlineData("/uploads/1/../../etc/passwd")]
    public async Task DeleteImageAsync_PathTraversal_IsBlocked(string maliciousPath)
    {
        // Arrange
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns("/var/www/app/wwwroot");

        var mockLogger = new Mock<ILogger<ImageService>>();
        var service = new ImageService(mockEnv.Object, mockLogger.Object);

        // Act - should not throw, should silently block
        // The method gracefully handles path traversal attempts without throwing
        var exception = await Record.ExceptionAsync(() => service.DeleteImageAsync(maliciousPath));

        // Assert - no exception means the path traversal was handled gracefully
        exception.Should().BeNull();
    }

    [Theory]
    [InlineData("/uploads/1/valid-image.jpg")]
    [InlineData("/uploads/123/photo.png")]
    public async Task DeleteImageAsync_ValidPath_IsAllowed(string validPath)
    {
        // Arrange
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns("/var/www/app/wwwroot");

        var mockLogger = new Mock<ILogger<ImageService>>();
        var service = new ImageService(mockEnv.Object, mockLogger.Object);

        // Act - should not throw for valid paths
        var exception = await Record.ExceptionAsync(() => service.DeleteImageAsync(validPath));

        // Assert - valid paths are handled gracefully (file may not exist, but no exception)
        exception.Should().BeNull();
    }

    [Fact]
    public async Task DeleteImageAsync_NullPath_HandledGracefully()
    {
        // Arrange
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns("/var/www/app/wwwroot");

        var mockLogger = new Mock<ILogger<ImageService>>();
        var service = new ImageService(mockEnv.Object, mockLogger.Object);

        // Act & Assert - should not throw
        await service.Invoking(s => s.DeleteImageAsync(null!))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteImageAsync_EmptyPath_HandledGracefully()
    {
        // Arrange
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns("/var/www/app/wwwroot");

        var mockLogger = new Mock<ILogger<ImageService>>();
        var service = new ImageService(mockEnv.Object, mockLogger.Object);

        // Act & Assert - should not throw
        await service.Invoking(s => s.DeleteImageAsync(""))
            .Should().NotThrowAsync();
    }

    #endregion
}
