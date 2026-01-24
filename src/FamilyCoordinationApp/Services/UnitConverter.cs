namespace FamilyCoordinationApp.Services;

public class UnitConverter
{
    // Conversion factors to base unit (cup for volume, gram for weight)
    private static readonly Dictionary<string, (string Family, decimal ToBase)> ConversionTable = new()
    {
        // Volume family (base: cup = 1)
        { "cup", ("volume", 1m) },
        { "cups", ("volume", 1m) },
        { "c", ("volume", 1m) },
        { "tbsp", ("volume", 0.0625m) }, // 1/16
        { "tablespoon", ("volume", 0.0625m) },
        { "tablespoons", ("volume", 0.0625m) },
        { "tsp", ("volume", 0.0208333333333333333333333333m) }, // 1/48
        { "teaspoon", ("volume", 0.0208333333333333333333333333m) },
        { "teaspoons", ("volume", 0.0208333333333333333333333333m) },
        { "fl oz", ("volume", 0.125m) }, // 1/8
        { "fluid ounce", ("volume", 0.125m) },
        { "fluid ounces", ("volume", 0.125m) },
        { "ml", ("volume", 0.00422675283773885889524670710m) }, // 1/236.588
        { "milliliter", ("volume", 0.00422675283773885889524670710m) },
        { "milliliters", ("volume", 0.00422675283773885889524670710m) },
        { "l", ("volume", 4.22675283773885889524670710m) }, // 1000/236.588
        { "liter", ("volume", 4.22675283773885889524670710m) },
        { "liters", ("volume", 4.22675283773885889524670710m) },

        // Weight family (base: gram = 1)
        { "g", ("weight", 1m) },
        { "gram", ("weight", 1m) },
        { "grams", ("weight", 1m) },
        { "kg", ("weight", 1000m) },
        { "kilogram", ("weight", 1000m) },
        { "kilograms", ("weight", 1000m) },
        { "oz", ("weight", 28.3495m) },
        { "ounce", ("weight", 28.3495m) },
        { "ounces", ("weight", 28.3495m) },
        { "lb", ("weight", 453.592m) },
        { "lbs", ("weight", 453.592m) },
        { "pound", ("weight", 453.592m) },
        { "pounds", ("weight", 453.592m) },

        // Count family (no cross-conversion)
        { "piece", ("count", 1m) },
        { "pieces", ("count", 1m) },
        { "clove", ("count", 1m) },
        { "cloves", ("count", 1m) },
        { "can", ("count", 1m) },
        { "cans", ("count", 1m) },
        { "bunch", ("count", 1m) },
    };

    /// <summary>
    /// Converts a quantity from one unit to another.
    /// </summary>
    /// <param name="quantity">The quantity to convert</param>
    /// <param name="fromUnit">The source unit</param>
    /// <param name="toUnit">The target unit</param>
    /// <returns>The converted quantity</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when units are unknown or from different families
    /// </exception>
    public decimal Convert(decimal quantity, string? fromUnit, string? toUnit)
    {
        if (string.IsNullOrWhiteSpace(fromUnit) || string.IsNullOrWhiteSpace(toUnit))
            return quantity;

        var from = NormalizeUnit(fromUnit);
        var to = NormalizeUnit(toUnit);

        if (from == to)
            return quantity;

        if (!ConversionTable.TryGetValue(from, out var fromInfo) ||
            !ConversionTable.TryGetValue(to, out var toInfo))
            throw new InvalidOperationException($"Cannot convert {from} to {to}");

        if (fromInfo.Family != toInfo.Family)
            throw new InvalidOperationException($"Cannot convert {fromInfo.Family} to {toInfo.Family}");

        // Convert to base unit, then to target unit
        var baseQuantity = quantity * fromInfo.ToBase;
        var result = baseQuantity / toInfo.ToBase;

        // Round to 10 decimal places to avoid floating-point precision issues
        return Math.Round(result, 10);
    }

    /// <summary>
    /// Finds a common unit for a list of units from the same family.
    /// Returns null if units are from different families or list is empty.
    /// Prefers the most frequently occurring unit.
    /// </summary>
    /// <param name="units">List of units to find common unit for</param>
    /// <returns>Common unit if all units are compatible, null otherwise</returns>
    public string? FindCommonUnit(List<string?> units)
    {
        var normalized = units
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => NormalizeUnit(u!))
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
            return null;

        // Check if all units are in same family
        var families = normalized
            .Where(u => ConversionTable.ContainsKey(u))
            .Select(u => ConversionTable[u].Family)
            .Distinct()
            .ToList();

        if (families.Count != 1)
            return null; // Mixed families, can't consolidate

        // Prefer the most common unit in the list
        var mostCommon = units
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => NormalizeUnit(u!))
            .GroupBy(u => u)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        return mostCommon;
    }

    /// <summary>
    /// Checks if two units can be converted between each other.
    /// </summary>
    /// <param name="fromUnit">The source unit</param>
    /// <param name="toUnit">The target unit</param>
    /// <returns>True if units are compatible for conversion, false otherwise</returns>
    public bool CanConvert(string? fromUnit, string? toUnit)
    {
        if (string.IsNullOrWhiteSpace(fromUnit) || string.IsNullOrWhiteSpace(toUnit))
            return false;

        var from = NormalizeUnit(fromUnit);
        var to = NormalizeUnit(toUnit);

        if (!ConversionTable.TryGetValue(from, out var fromInfo) ||
            !ConversionTable.TryGetValue(to, out var toInfo))
            return false;

        return fromInfo.Family == toInfo.Family;
    }

    /// <summary>
    /// Normalizes a unit string for lookup (lowercase, trimmed).
    /// </summary>
    private string NormalizeUnit(string unit)
    {
        return unit.ToLowerInvariant().Trim();
    }
}
