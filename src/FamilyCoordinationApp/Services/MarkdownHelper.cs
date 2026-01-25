using Ganss.Xss;
using Markdig;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Helper for rendering Markdown safely by sanitizing HTML output to prevent XSS attacks.
/// </summary>
public static class MarkdownHelper
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        
        // Allow common formatting tags from Markdown
        sanitizer.AllowedTags.Add("h1");
        sanitizer.AllowedTags.Add("h2");
        sanitizer.AllowedTags.Add("h3");
        sanitizer.AllowedTags.Add("h4");
        sanitizer.AllowedTags.Add("h5");
        sanitizer.AllowedTags.Add("h6");
        sanitizer.AllowedTags.Add("p");
        sanitizer.AllowedTags.Add("br");
        sanitizer.AllowedTags.Add("hr");
        sanitizer.AllowedTags.Add("strong");
        sanitizer.AllowedTags.Add("b");
        sanitizer.AllowedTags.Add("em");
        sanitizer.AllowedTags.Add("i");
        sanitizer.AllowedTags.Add("u");
        sanitizer.AllowedTags.Add("s");
        sanitizer.AllowedTags.Add("del");
        sanitizer.AllowedTags.Add("code");
        sanitizer.AllowedTags.Add("pre");
        sanitizer.AllowedTags.Add("blockquote");
        sanitizer.AllowedTags.Add("ul");
        sanitizer.AllowedTags.Add("ol");
        sanitizer.AllowedTags.Add("li");
        sanitizer.AllowedTags.Add("a");
        sanitizer.AllowedTags.Add("img");
        sanitizer.AllowedTags.Add("table");
        sanitizer.AllowedTags.Add("thead");
        sanitizer.AllowedTags.Add("tbody");
        sanitizer.AllowedTags.Add("tr");
        sanitizer.AllowedTags.Add("th");
        sanitizer.AllowedTags.Add("td");
        
        // Allow safe attributes
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("src");
        sanitizer.AllowedAttributes.Add("alt");
        sanitizer.AllowedAttributes.Add("title");
        sanitizer.AllowedAttributes.Add("class");
        
        // Only allow safe URL schemes
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        
        return sanitizer;
    }

    /// <summary>
    /// Converts Markdown to sanitized HTML safe for rendering.
    /// </summary>
    /// <param name="markdown">The raw Markdown input.</param>
    /// <returns>Sanitized HTML string.</returns>
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var html = Markdown.ToHtml(markdown);
        return Sanitizer.Sanitize(html);
    }
}
