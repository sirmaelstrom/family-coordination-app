namespace FamilyCoordinationApp.Services;

public class ParsedIngredient
{
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }

    /// <summary>
    /// Returns true if the ingredient name is not empty. A complete parse includes at minimum a name.
    /// </summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name);
}

public interface IIngredientParser
{
    ParsedIngredient ParseIngredient(string input);
}

public class IngredientParser : IIngredientParser
{
    private static readonly HashSet<string> KnownUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        // Volume - US
        "cup", "cups", "tbsp", "tablespoon", "tablespoons", "tsp", "teaspoon", "teaspoons",
        "oz", "ounce", "ounces", "fl oz",
        // Volume - Metric
        "ml", "liter", "liters", "l",
        // Weight - US
        "lb", "lbs", "pound", "pounds",
        // Weight - Metric
        "g", "gram", "grams", "kg", "kilogram", "kilograms",
        // Count
        "each", "whole", "piece", "pieces", "clove", "cloves", "can", "cans",
        "package", "packages", "bunch", "bunches"
    };

    private static readonly Dictionary<char, decimal> UnicodeFractions = new()
    {
        { '¼', 0.25m }, { '½', 0.5m }, { '¾', 0.75m },
        { '⅐', 1m/7m }, { '⅑', 1m/9m }, { '⅒', 0.1m },
        { '⅓', 1m/3m }, { '⅔', 2m/3m },
        { '⅕', 0.2m }, { '⅖', 0.4m }, { '⅗', 0.6m }, { '⅘', 0.8m },
        { '⅙', 1m/6m }, { '⅚', 5m/6m },
        { '⅛', 0.125m }, { '⅜', 0.375m }, { '⅝', 0.625m }, { '⅞', 0.875m }
    };

    public ParsedIngredient ParseIngredient(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Input cannot be empty", nameof(input));

        var result = new ParsedIngredient();
        var working = input.Trim();

        // Extract notes from parentheses and commas
        (working, result.Notes) = ExtractNotes(working);

        // Normalize Unicode fractions to ASCII
        working = NormalizeUnicodeFractions(working);

        // Try to extract quantity
        var tokens = working.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            result.Name = input.Trim();
            return result;
        }

        int position = 0;

        // Try to parse quantity (could be decimal, fraction, mixed fraction, or range)
        if (TryParseQuantity(tokens, ref position, out decimal? quantity))
        {
            result.Quantity = quantity;
        }

        // Try to extract unit (could be multi-word like "fl oz")
        if (position < tokens.Length)
        {
            // Try two-word unit first
            if (position + 1 < tokens.Length)
            {
                var twoWordUnit = tokens[position] + " " + tokens[position + 1];
                if (IsKnownUnit(twoWordUnit))
                {
                    result.Unit = twoWordUnit;
                    position += 2;
                }
                else if (IsKnownUnit(tokens[position]))
                {
                    result.Unit = tokens[position];
                    position++;
                }
            }
            else if (IsKnownUnit(tokens[position]))
            {
                result.Unit = tokens[position];
                position++;
            }
        }

        // Remaining tokens form the ingredient name
        if (position < tokens.Length)
        {
            result.Name = string.Join(" ", tokens[position..]);
        }

        return result;
    }

    private (string cleaned, string? notes) ExtractNotes(string input)
    {
        var notes = new List<string>();
        var working = input;

        // Extract parentheses content
        while (true)
        {
            var startParen = working.IndexOf('(');
            if (startParen == -1) break;

            var endParen = working.IndexOf(')', startParen);
            if (endParen == -1) break;

            var content = working.Substring(startParen + 1, endParen - startParen - 1).Trim();
            if (!string.IsNullOrEmpty(content))
                notes.Add(content);

            working = working.Remove(startParen, endParen - startParen + 1);
        }

        // Extract comma-separated notes
        var commaIndex = working.IndexOf(',');
        if (commaIndex != -1)
        {
            var afterComma = working.Substring(commaIndex + 1).Trim();
            if (!string.IsNullOrEmpty(afterComma))
                notes.Add(afterComma);
            working = working.Substring(0, commaIndex);
        }

        working = working.Trim();
        var combinedNotes = notes.Count > 0 ? string.Join(", ", notes) : null;

        return (working, combinedNotes);
    }

    private string NormalizeUnicodeFractions(string input)
    {
        foreach (var kvp in UnicodeFractions)
        {
            if (input.Contains(kvp.Key))
            {
                // Add space before Unicode fraction to separate it from preceding digit (e.g., "1½" -> "1 0.5")
                // This allows proper mixed fraction parsing
                input = input.Replace(kvp.Key.ToString(), " " + kvp.Value.ToString("0.#####"));
            }
        }
        return input;
    }

    private bool TryParseQuantity(string[] tokens, ref int position, out decimal? quantity)
    {
        quantity = null;
        if (position >= tokens.Length)
            return false;

        var token = tokens[position];

        // Try range format: "2-3"
        if (token.Contains('-') && !token.StartsWith('-'))
        {
            var parts = token.Split('-');
            if (parts.Length == 2 &&
                decimal.TryParse(parts[0], out decimal start) &&
                decimal.TryParse(parts[1], out decimal end))
            {
                quantity = (start + end) / 2m;
                position++;
                return true;
            }
        }

        // Try simple decimal
        if (decimal.TryParse(token, out decimal decimalValue))
        {
            quantity = decimalValue;
            position++;

            // Check if next token is a fraction or decimal < 1 (for mixed fractions like "1 1/2" or "1 0.5")
            if (position < tokens.Length)
            {
                if (tokens[position].Contains('/') && TryParseFraction(tokens[position], out decimal fractionValue))
                {
                    quantity += fractionValue;
                    position++;
                }
                else if (decimal.TryParse(tokens[position], out decimal nextDecimal) && nextDecimal < 1)
                {
                    // This handles normalized Unicode fractions like "1½" -> "1 0.5"
                    quantity += nextDecimal;
                    position++;
                }
            }

            return true;
        }

        // Try fraction format: "1/2"
        if (token.Contains('/'))
        {
            if (TryParseFraction(token, out decimal fractionValue))
            {
                quantity = fractionValue;
                position++;
                return true;
            }
        }

        return false;
    }

    private bool TryParseFraction(string input, out decimal value)
    {
        value = 0;
        var parts = input.Split('/');
        if (parts.Length != 2)
            return false;

        if (!decimal.TryParse(parts[0], out decimal numerator))
            return false;

        if (!decimal.TryParse(parts[1], out decimal denominator))
            return false;

        if (denominator == 0)
            return false;

        value = numerator / denominator;
        return true;
    }

    private bool IsKnownUnit(string token)
    {
        return KnownUnits.Contains(token);
    }
}
