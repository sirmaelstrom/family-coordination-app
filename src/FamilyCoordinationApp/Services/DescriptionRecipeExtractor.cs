using System.Text;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Heuristic parser that extracts structured recipes from YouTube-style
/// video descriptions. No LLM involved — pattern matching only.
/// </summary>
public class DescriptionRecipeExtractor(ILogger<DescriptionRecipeExtractor> logger) : IDescriptionRecipeExtractor
{
    private static readonly string[] IngredientHeaders =
    [
        "ingredients", "what you'll need", "what you will need", "you will need", "you'll need"
    ];

    private static readonly string[] InstructionHeaders =
    [
        "instructions", "directions", "method", "steps", "preparation"
    ];

    private static readonly Regex ListItemRegex = new(@"^\s*(?:[-*•●▪]|(?:\d+[\.\)]))\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex QuantityRegex = new(
        @"^\s*(?:\d+(?:[\.\/]\d+)?|\d+\s+\d+/\d+|½|¼|¾|⅓|⅔|⅛|⅜|⅝|⅞)\s*\w",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderRegex = new(@"^\s*([A-Za-z][\w '\-]{1,40})\s*:?\s*$", RegexOptions.Compiled);
    private static readonly Regex SocialNoiseRegex = new(
        @"(instagram|twitter|tiktok|facebook|patreon|sponsor|subscribe|merch|cookbook|http|www\.|@\w+|#\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public RecipeSchema? ExtractFromDescription(string? description, string? videoTitle = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var lines = description.Replace("\r\n", "\n").Split('\n');
        var ingredients = new List<string>();
        var instructions = new List<string>();

        var mode = Section.None;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                // Keep mode across blank lines — recipe lists often have blank rows.
                continue;
            }

            var headerKind = ClassifyHeader(line);
            if (headerKind == Section.Ingredients || headerKind == Section.Instructions)
            {
                mode = headerKind;
                continue;
            }

            // If we hit a clearly unrelated header (e.g., "Equipment:", "Sponsor:"), reset mode.
            if (headerKind == Section.OtherHeader)
            {
                mode = Section.None;
                continue;
            }

            switch (mode)
            {
                case Section.Ingredients:
                    if (IsIngredientLine(line))
                        ingredients.Add(CleanListItem(line));
                    break;

                case Section.Instructions:
                    if (IsInstructionLine(line))
                        instructions.Add(CleanListItem(line));
                    break;
            }
        }

        if (ingredients.Count < 2 || instructions.Count < 1)
        {
            logger.LogDebug(
                "Description extraction rejected: ingredients={IngredientCount}, instructions={InstructionCount}",
                ingredients.Count, instructions.Count);
            return null;
        }

        var schema = new RecipeSchema
        {
            Name = !string.IsNullOrWhiteSpace(videoTitle) ? videoTitle.Trim() : "Imported Recipe",
            Description = null,
            RecipeIngredient = ingredients.ToArray(),
            RecipeInstructions = FormatInstructions(instructions),
            PrepTime = ExtractIsoDuration(description, "prep"),
            CookTime = ExtractIsoDuration(description, "cook"),
            RecipeYield = ExtractServings(description)
        };

        return schema;
    }

    private static Section ClassifyHeader(string line)
    {
        var normalized = line.TrimEnd(':', '-', ' ').Trim().ToLowerInvariant();

        foreach (var header in IngredientHeaders)
            if (normalized == header || normalized.StartsWith(header + ":"))
                return Section.Ingredients;

        foreach (var header in InstructionHeaders)
            if (normalized == header || normalized.StartsWith(header + ":"))
                return Section.Instructions;

        // Short standalone header-like lines ("Equipment:", "Tools:")
        if (line.EndsWith(':') && line.Length < 40 && HeaderRegex.IsMatch(line))
            return Section.OtherHeader;

        return Section.None;
    }

    private static bool IsIngredientLine(string line)
    {
        if (SocialNoiseRegex.IsMatch(line))
            return false;

        if (ListItemRegex.IsMatch(line))
            return true;

        if (QuantityRegex.IsMatch(line))
            return true;

        return false;
    }

    private static bool IsInstructionLine(string line)
    {
        if (SocialNoiseRegex.IsMatch(line))
            return false;

        if (ListItemRegex.IsMatch(line))
            return true;

        // Paragraph-style steps — accept lines with enough content.
        return line.Length >= 15 && line.Any(char.IsLetter);
    }

    private static string CleanListItem(string line)
    {
        var match = ListItemRegex.Match(line);
        if (match.Success)
            return match.Groups[1].Value.Trim();
        return line.Trim();
    }

    private static string FormatInstructions(List<string> steps)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0)
                sb.Append("\n\n");
            sb.Append(i + 1).Append(". ").Append(steps[i]);
        }
        return sb.ToString();
    }

    private static string? ExtractIsoDuration(string text, string kind)
    {
        // 1) Locate the keyword snippet (e.g. "Cook time: 30 minutes" on one line).
        var keywordPattern = kind.Equals("prep", StringComparison.OrdinalIgnoreCase)
            ? @"\bprep\b(?:\s*time)?[^\n\r]*"
            : @"\bcook\b(?:\s*time)?[^\n\r]*";

        var keywordMatch = Regex.Match(text, keywordPattern, RegexOptions.IgnoreCase);
        if (!keywordMatch.Success)
            return null;

        var snippet = keywordMatch.Value;

        // 2) Extract optional hours and minutes from the snippet.
        var hoursMatch = Regex.Match(snippet, @"(\d+)\s*(?:h|hr|hours?)\b", RegexOptions.IgnoreCase);
        var minutesMatch = Regex.Match(snippet, @"(\d+)\s*(?:m|min|minutes?)\b", RegexOptions.IgnoreCase);

        var hours = hoursMatch.Success && int.TryParse(hoursMatch.Groups[1].Value, out var h) ? h : 0;
        var minutes = minutesMatch.Success && int.TryParse(minutesMatch.Groups[1].Value, out var m) ? m : 0;

        if (hours == 0 && minutes == 0)
            return null;

        var sb = new StringBuilder("PT");
        if (hours > 0) sb.Append(hours).Append('H');
        if (minutes > 0) sb.Append(minutes).Append('M');
        return sb.ToString();
    }

    private static string? ExtractServings(string text)
    {
        // "Serves 4", "Yield: 6 servings", "Makes 8", "4 servings"
        var patterns = new[]
        {
            @"serves\s*:?\s*(\d+)",
            @"yield\s*:?\s*(\d+)",
            @"makes\s*:?\s*(\d+)",
            @"(\d+)\s+servings?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private enum Section
    {
        None,
        Ingredients,
        Instructions,
        OtherHeader
    }
}
