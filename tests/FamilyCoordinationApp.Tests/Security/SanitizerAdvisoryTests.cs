using AngleSharp.Html.Parser;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using System.Text.Json;

namespace FamilyCoordinationApp.Tests.Security;

public class SanitizerAdvisoryTests
{
    [Fact]
    public void AnnotationXml_UsesHtmlParsingRules()
    {
        // GHSA-pgww-w46g-26qg: a browser sees this img, so the sanitizer's parser must too.
        const string payload = "<math><annotation-xml encoding=\"text/html\"><title><a encoding=\"</title><img src=x onerror=alert()>\"></annotation-xml></math>";
        var document = new HtmlParser().ParseDocument(payload);
        document.QuerySelector("img").Should().NotBeNull();
    }

    [Theory]
    [InlineData("<div><template shadowrootmode=\"open\"><script>alert('xss')</script></template></div>")]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><title><a encoding=\"</title><img src=x onerror=alert()>\"></annotation-xml></math>")]
    public void AdvisoryPayloads_AreSafeAfterBrowserReparse(string payload)
    {
        var html = MarkdownHelper.ToSafeHtml(payload);
        var document = new HtmlParser().ParseDocument(html);
        document.QuerySelector("script, template, math, [onerror], [shadowrootmode]").Should().BeNull();
    }

    [Theory]
    [InlineData("RecipeFull/recipe.json", "<ol>\n<li>Combine oats and milk.</li>\n<li>Chill overnight.</li>\n</ol>\n")]
    [InlineData("RecipeDraft/draft.json", "<p>Simmer until done.</p>\n")]
    [InlineData("RecipeDraft/save-request.json", "<p>Simmer until done.</p>\n")]
    [InlineData("RecipeWrite/request.json", "<p>Mix and fry.</p>\n")]
    public void RealRecipeFixtures_PreserveSanitizedHtml(string fixture, string expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var instructions = json.RootElement.GetProperty("instructions").GetString();
        var html = MarkdownHelper.ToSafeHtml(instructions);
        html.Replace("\r\n", "\n").Should().Be(expected);

        // Optional evidence export for comparing the same fixtures across package versions.
        var sink = Environment.GetEnvironmentVariable("FCA_SANITIZER_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(sink))
        {
            Directory.CreateDirectory(sink);
            File.WriteAllText(Path.Combine(sink, fixture.Replace('/', '-') + ".html"), html);
        }
    }
}
