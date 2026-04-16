using FluentAssertions;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class YouTubeUrlHelperTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=abc")]
    [InlineData("https://www.youtube.com/shorts/abc123")]
    [InlineData("https://www.youtube.com/embed/abc123")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=abc&t=120s&utm_source=share")]
    public void IsYouTubeUrl_RecognisesYouTubeUrls(string url)
    {
        YouTubeUrlHelper.IsYouTubeUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.allrecipes.com/recipe/21014/")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://youtube.evil.com/watch?v=abc")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsYouTubeUrl_RejectsNonYouTubeUrls(string? url)
    {
        YouTubeUrlHelper.IsYouTubeUrl(url).Should().BeFalse();
    }
}
