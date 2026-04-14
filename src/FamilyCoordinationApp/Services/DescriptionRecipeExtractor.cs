using System.Text.RegularExpressions;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class DescriptionRecipeExtractor : IDescriptionRecipeExtractor
{
    private static readonly Regex IngredientHeaderRegex = new(
        @"^(ingredients?|what\s+you(?:'ll|'ll)?\s+need|you\s+will\s+need|you\s+need)\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InstructionHeaderRegex = new(
        @"^(instructions?|directions?|method|steps?|how\s+to\s+make|preparation)\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches lines starting with list markers OR bare lines with a quantity + known unit
    private static readonly Regex QuantityUnitPattern = new(
        @"^[\-•*]?\s*\d[\d/\s]*\s*(cup|cups|tbsp|tablespoon|tablespoons|tsp|teaspoon|teaspoons|oz|ounce|ounces|lb|lbs|pound|pounds|g\b|gram|grams|kg|ml|liter|liters|clove|cloves|can|cans|bunch|bunches|piece|pieces|pinch|dash|handful)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListBulletPattern = new(
        @"^[\-•*]\s+.+",
        RegexOptions.Compiled);

    private static readonly Regex NumberedStepPattern = new(
        @"^\d+[.)]\s+.+",
        RegexOptions.Compiled);

    private static readonly Regex ServingsPattern = new(
        @"(?:serves?|yields?|makes?|servings?)\s*:?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrepTimePattern = new(
        @"prep\s*(?:time)?\s*:?\s*(\d+)\s*(min|minute|minutes|hr|hour|hours)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CookTimePattern = new(
        @"cook\s*(?:time)?\s*:?\s*(\d+)\s*(min|minute|minutes|hr|hour|hours)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OtherSectionHeaderRegex = new(
        @"^(notes?|tips?|nutrition|storage|video|subscribe|follow|links?|related|print|equipment)\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Lines that indicate recipe content has ended (social/sponsor lines)
    private static readonly Regex NonRecipeLinePattern = new(
        @"(@\w{3,}|#\w{3,}|instagram\.com|youtube\.com|twitter\.com|facebook\.com|tiktok\.com|sponsored\s+by|affiliate|business\s+inquiries|follow\s+me|subscribe\s+(?:for|now|below)|buy\s+my)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RecipeSchema? ExtractFromDescription(string description, string? videoTitle = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var lines = description.Split('\n');

        var ingredients = ExtractIngredients(lines);
        var instructions = ExtractInstructions(lines);

        if (ingredients.Count < 2 || instructions == null)
            return null;

        var schema = new RecipeSchema
        {
            Name = videoTitle,
            RecipeIngredient = ingredients.ToArray(),
            RecipeInstructions = instructions,
            PrepTime = ExtractTime(description, PrepTimePattern),
            CookTime = ExtractTime(description, CookTimePattern),
        };

        var servings = ExtractServings(description);
        if (servings != null)
            schema.RecipeYield = servings;

        return schema;
    }

    private static List<string> ExtractIngredients(string[] lines)
    {
        var ingredients = new List<string>();
        var inSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (IngredientHeaderRegex.IsMatch(line))
            {
                inSection = true;
                continue;
            }

            if (!inSection)
                continue;

            // Stop at another section header
            if (InstructionHeaderRegex.IsMatch(line) || OtherSectionHeaderRegex.IsMatch(line))
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (ListBulletPattern.IsMatch(line) || QuantityUnitPattern.IsMatch(line))
            {
                var cleaned = Regex.Replace(line, @"^[\-•*]\s*", "").Trim();
                ingredients.Add(cleaned);
            }
        }

        return ingredients;
    }

    private static string? ExtractInstructions(string[] lines)
    {
        var steps = new List<string>();
        var paragraphBuffer = new List<string>();
        var inSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (InstructionHeaderRegex.IsMatch(line))
            {
                inSection = true;
                continue;
            }

            if (!inSection)
                continue;

            // Stop at another section header or non-recipe social/sponsor content
            if (OtherSectionHeaderRegex.IsMatch(line) || NonRecipeLinePattern.IsMatch(line))
            {
                FlushBuffer(paragraphBuffer, steps);
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushBuffer(paragraphBuffer, steps);
                continue;
            }

            if (NumberedStepPattern.IsMatch(line))
            {
                FlushBuffer(paragraphBuffer, steps);
                var stepText = Regex.Replace(line, @"^\d+[.)]\s*", "").Trim();
                steps.Add(stepText);
            }
            else
            {
                paragraphBuffer.Add(line);
            }
        }

        FlushBuffer(paragraphBuffer, steps);

        if (steps.Count == 0)
            return null;

        // Format steps as numbered list joined by double newlines
        return string.Join("\n\n", steps.Select((s, i) => $"{i + 1}. {s}"));
    }

    private static void FlushBuffer(List<string> buffer, List<string> steps)
    {
        if (buffer.Count > 0)
        {
            steps.Add(string.Join(" ", buffer));
            buffer.Clear();
        }
    }

    private static string? ExtractTime(string description, Regex pattern)
    {
        var match = pattern.Match(description);
        if (!match.Success)
            return null;

        var amount = int.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToLowerInvariant();

        return unit.StartsWith("hr") || unit.StartsWith("hour")
            ? $"PT{amount}H"
            : $"PT{amount}M";
    }

    private static string? ExtractServings(string description)
    {
        var match = ServingsPattern.Match(description);
        return match.Success ? match.Groups[1].Value : null;
    }
}
