using System.Text.Json;
using FluentAssertions;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class YtDlpServiceTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "YtDlp", name));

    [Fact]
    public void BuildArguments_IncludesSubtitleFlags()
    {
        var args = YtDlpService.BuildArguments("https://youtu.be/abc", "/tmp/abc/%(id)s");

        args.Should().Contain("--write-info-json");
        args.Should().Contain("--write-sub");
        args.Should().Contain("--write-auto-sub");
        args.Should().Contain("--skip-download");
        args.Should().Contain("srv1");
        args.Should().Contain("https://youtu.be/abc");
    }

    [Fact]
    public void SampleMetadata_Deserializes()
    {
        var json = Fixture("sample-video.meta.json");
        var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().Be("dQw4w9WgXcQ");
        metadata.Title.Should().Be("Perfect Chocolate Chip Cookies");
        metadata.Description.Should().Contain("Ingredients");
        metadata.Duration.Should().Be(420.0);
        metadata.Channel.Should().Be("Test Chef");
    }

    [Fact]
    public void NoCaptionsMetadata_Deserializes()
    {
        var json = Fixture("no-captions.meta.json");
        var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().Be("noSubsXYZ");
        metadata.Description.Should().Be("No captions available.");
    }

    [Fact]
    public void ParseSrv1_StripsHtmlAndCollapsesWhitespace()
    {
        var xml = Fixture("sample-video.en.srv1");
        var transcript = YtDlpService.ParseSrv1(xml);

        transcript.Should().NotBeNull();
        transcript!.Should().Contain("chocolate chip cookies");
        transcript.Should().NotContain("<font");
        transcript.Should().NotContain("</font>");
        transcript.Should().NotContain("  "); // whitespace collapsed
    }

    [Fact]
    public void ParseSrv1_EmptyOrInvalid_ReturnsNull()
    {
        YtDlpService.ParseSrv1("").Should().BeNull();
        YtDlpService.ParseSrv1("<not xml").Should().BeNull();
    }
}
