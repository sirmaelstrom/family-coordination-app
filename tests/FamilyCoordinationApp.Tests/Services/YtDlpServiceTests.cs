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
        args.Should().Contain("srv1/srv3");
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
    public void ParseSubtitle_Srv1_StripsHtmlAndCollapsesWhitespace()
    {
        var xml = Fixture("sample-video.en.srv1");
        var transcript = YtDlpService.ParseSubtitle(xml);

        transcript.Should().NotBeNull();
        transcript!.Should().Contain("chocolate chip cookies");
        transcript.Should().NotContain("<font");
        transcript.Should().NotContain("</font>");
        transcript.Should().NotContain("  "); // whitespace collapsed
    }

    [Fact]
    public void ParseSubtitle_Srv3_ExtractsFromPElements()
    {
        var xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <timedtext format="3">
          <body>
            <p t="0" d="2000">Preheat the oven to 350.</p>
            <p t="2000" d="3000">Mix flour and sugar together.</p>
          </body>
        </timedtext>
        """;

        var transcript = YtDlpService.ParseSubtitle(xml);

        transcript.Should().Be("Preheat the oven to 350. Mix flour and sugar together.");
    }

    [Fact]
    public void ParseSubtitle_EmptyOrInvalid_ReturnsNull()
    {
        YtDlpService.ParseSubtitle("").Should().BeNull();
        YtDlpService.ParseSubtitle("<not xml").Should().BeNull();
    }
}
