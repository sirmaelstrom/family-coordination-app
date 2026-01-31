namespace FamilyCoordinationApp.Constants;

public static class CategoryDefaults
{
    public const string DefaultCategory = "Pantry";
    public const string DefaultCategoryColor = "#808080";

    /// <summary>
    /// Standard shopping list categories in store-layout order.
    /// Order follows typical grocery store layout: perimeter first (produce, bakery, meat, dairy),
    /// then frozen, then center aisles (pantry, spices, beverages), with Other last.
    /// </summary>
    public static readonly string[] StandardCategories =
    {
        "Produce",
        "Bakery",
        "Meat",
        "Dairy",
        "Frozen",
        "Pantry",
        "Spices",
        "Beverages",
        "Other"
    };

    /// <summary>
    /// Gets the sort order for a category (lower = earlier in store).
    /// </summary>
    public static int GetCategoryOrder(string category)
    {
        for (int i = 0; i < StandardCategories.Length; i++)
        {
            if (string.Equals(StandardCategories[i], category, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return 99; // Unknown categories go last
    }
}
