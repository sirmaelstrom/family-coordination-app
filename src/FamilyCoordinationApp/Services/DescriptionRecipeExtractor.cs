using System.Text;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class DescriptionRecipeExtractor : IDescriptionRecipeExtractor
{
    private static readonly string[] IngredientHeaders =
    [
        "ingredients", "ingredient list", "what you'll need", "what you will need",
        "you will need", "you'll need", "you need"
    ];

    private static readonly string[] InstructionHeaders =
    [
        "instructions", "directions", "method", "steps", "preparation",
        "how to make", "how to prepare", "to make this recipe", "how to cook"
    ];

    private static readonly string[] AllSectionHeaders =
    [
        "ingredients", "ingredient list", "what you'll need", "what you will need",
        "you will need", "you'll need", "you need",
        "instructions", "directions", "method", "steps", "preparation",
        "how to make", "how to prepare", "to make this recipe", "how to cook",
        "notes", "tips", "nutrition", "equipment", "storage", "serving",
        "variations", "substitutions"
    ];

    // Quantity pattern: starts with digit (possibly fraction-like)
    private static readonly Regex QuantityStartPattern = new(@"^\d", RegexOptions.Compiled);

    // Numbered step: "1. " or "1) "
    private static readonly Regex NumberedStepPattern = new(@"^\d+[.)]\s+\S", RegexOptions.Compiled);

    public RecipeSchema? ExtractFromDescription(string description, string? videoTitle = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var lines = description.ReplaceLineEndings("\n").Split('\n');

        // Find ingredient section
        var (ingredientHeaderIndex, ingredientContentStart) = FindSection(lines, IngredientHeaders);
        if (ingredientContentStart < 0)
            return null;

        int ingredientSectionEnd = FindSectionEnd(lines, ingredientContentStart);
        var ingredients = CollectIngredients(lines, ingredientContentStart, ingredientSectionEnd);

        if (ingredients.Count < 2)
            return null;

        // Find instruction section
        var (_, instructionContentStart) = FindSection(lines, InstructionHeaders);
        if (instructionContentStart < 0)
            return null;

        int instructionSectionEnd = FindSectionEnd(lines, instructionContentStart);
        var steps = CollectInstructionSteps(lines, instructionContentStart, instructionSectionEnd);

        if (steps.Count == 0)
            return null;

        var schema = new RecipeSchema
        {
            Name = videoTitle ?? TryExtractTitleBefore(lines, ingredientHeaderIndex),
            RecipeIngredient = [.. ingredients],
            RecipeInstructions = FormatSteps(steps)
        };

        ExtractTimeAndYield(description, schema);
        return schema;
    }

    private static (int headerIndex, int contentStart) FindSection(string[] lines, string[] headers)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsHeaderLine(lines[i], headers))
                return (i, i + 1);
        }
        return (-1, -1);
    }

    private static bool IsHeaderLine(string line, string[] headers)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        foreach (var header in headers)
        {
            if (trimmed.Equals(header, StringComparison.OrdinalIgnoreCase))
                return true;
            if (trimmed.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int FindSectionEnd(string[] lines, int contentStart)
    {
        int consecutiveBlanks = 0;
        int lastContentLine = contentStart - 1;

        for (int i = contentStart; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                consecutiveBlanks++;
                if (consecutiveBlanks >= 2)
                    return lastContentLine + 1;
            }
            else
            {
                consecutiveBlanks = 0;
                var trimmed = lines[i].Trim();
                if (trimmed == "---" || trimmed == "***" || trimmed == "===" ||
                    IsHeaderLine(lines[i], AllSectionHeaders))
                    return i;
                lastContentLine = i;
            }
        }

        return lines.Length;
    }

    private static List<string> CollectIngredients(string[] lines, int start, int end)
    {
        var ingredients = new List<string>();

        for (int i = start; i < end && i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();
            bool hasBullet = trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '•' || trimmed[0] == '*');
            var content = hasBullet ? trimmed.Substring(1).Trim() : trimmed;

            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (IsIngredientLine(content, hasBullet))
                ingredients.Add(content);
        }

        return ingredients;
    }

    private static bool IsIngredientLine(string content, bool hadBullet)
    {
        if (IsNonIngredientContent(content))
            return false;

        if (QuantityStartPattern.IsMatch(content))
            return true;

        // Accept bullet items that survived the non-ingredient filter
        return hadBullet;
    }

    private static bool IsNonIngredientContent(string content)
    {
        if (content.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("@") ||
            content.StartsWith("#"))
            return true;

        var lower = content.ToLowerInvariant();
        return lower.Contains("instagram") ||
               lower.Contains("twitter") ||
               lower.Contains("facebook") ||
               lower.Contains("subscribe") ||
               lower.Contains("follow me") ||
               lower.Contains("youtube") ||
               lower.Contains("tiktok");
    }

    private static List<string> CollectInstructionSteps(string[] lines, int start, int end)
    {
        var steps = new List<string>();
        var current = new StringBuilder();

        void FlushCurrent()
        {
            var text = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                steps.Add(text);
            current.Clear();
        }

        for (int i = start; i < end && i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushCurrent();
                continue;
            }

            var trimmed = line.Trim();
            if (NumberedStepPattern.IsMatch(trimmed))
            {
                FlushCurrent();
                current.Append(Regex.Replace(trimmed, @"^\d+[.)]\s+", ""));
            }
            else if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '•' || trimmed[0] == '*'))
            {
                FlushCurrent();
                current.Append(trimmed.Substring(1).Trim());
            }
            else
            {
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(trimmed);
            }
        }

        FlushCurrent();
        return steps;
    }

    private static string FormatSteps(List<string> steps) =>
        string.Join("\n\n", steps.Select((s, i) => $"{i + 1}. {s}"));

    private static string? TryExtractTitleBefore(string[] lines, int headerLineIndex)
    {
        for (int i = headerLineIndex - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!string.IsNullOrWhiteSpace(line) && line.Length > 3 &&
                !line.StartsWith("#") && !line.StartsWith("@") && !line.StartsWith("http"))
                return line;
        }
        return null;
    }

    private static void ExtractTimeAndYield(string description, RecipeSchema schema)
    {
        schema.PrepTime = ParseKeywordTime(description, @"prep(?:\s+time)?");
        schema.CookTime = ParseKeywordTime(description, @"cook(?:ing)?(?:\s+time)?");

        var servingsMatch = Regex.Match(description,
            @"(?:serves?|yields?|makes?|servings?):?\s*(\d+)",
            RegexOptions.IgnoreCase);
        if (servingsMatch.Success)
            schema.RecipeYield = servingsMatch.Groups[1].Value;
    }

    private static string? ParseKeywordTime(string text, string keywordPattern)
    {
        var match = Regex.Match(text,
            $@"{keywordPattern}:?\s*(\d+)\s*(hours?|hrs?|min(?:utes?)?)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        var value = int.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToLowerInvariant();

        int hours, minutes;
        if (unit.StartsWith("h"))
        {
            hours = value;
            var minMatch = Regex.Match(text.Substring(match.Index + match.Length),
                @"^\s*(\d+)\s*min(?:utes?)?", RegexOptions.IgnoreCase);
            minutes = minMatch.Success ? int.Parse(minMatch.Groups[1].Value) : 0;
        }
        else
        {
            hours = 0;
            minutes = value;
        }

        if (hours == 0 && minutes == 0)
            return null;

        if (hours > 0 && minutes > 0)
            return $"PT{hours}H{minutes}M";
        if (hours > 0)
            return $"PT{hours}H";
        return $"PT{minutes}M";
    }
}
