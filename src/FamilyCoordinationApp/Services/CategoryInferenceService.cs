using FamilyCoordinationApp.Constants;
namespace FamilyCoordinationApp.Services;

/// <summary>
/// Infers ingredient categories from ingredient names using keyword matching.
/// Falls back to "Pantry" for unknown ingredients.
/// </summary>
public interface ICategoryInferenceService
{
    string InferCategory(string ingredientName);
}

public class CategoryInferenceService : ICategoryInferenceService
{
    // Category mappings using List to preserve order - more specific/common categories first
    // Order: Produce and common items first, then proteins, then pantry items
    private static readonly List<(string Category, string[] Keywords)> CategoryKeywords =
    [
        // Produce first - most common fresh items
        ("Produce", new[]
        {
            "onion", "garlic", "tomato", "potato", "carrot", "celery", "lettuce",
            "spinach", "kale", "broccoli", "cauliflower", "pepper", "bell pepper",
            "jalapeño", "jalapeno", "cucumber", "zucchini", "squash", "eggplant",
            "mushroom", "corn", "pea", "bean", "green bean", "asparagus", "artichoke",
            "cabbage", "brussels sprout", "leek", "shallot", "scallion", "green onion",
            "radish", "beet", "turnip", "parsnip", "sweet potato", "yam",
            "avocado", "lime", "lemon", "orange", "apple", "banana", "berry",
            "strawberry", "blueberry", "raspberry", "blackberry", "grape", "melon",
            "watermelon", "cantaloupe", "pineapple", "mango", "papaya", "peach",
            "pear", "plum", "cherry", "apricot", "fig", "date", "kiwi",
            "ginger", "fresh herb", "cilantro", "parsley", "basil", "mint", "dill",
            "rosemary", "thyme", "sage", "oregano", "chive"
        }),

        // Dairy - common cooking ingredients
        ("Dairy", new[]
        {
            "milk", "cream", "butter", "cheese", "yogurt", "sour cream", "cottage cheese",
            "ricotta", "mozzarella", "cheddar", "parmesan", "feta", "gouda", "brie",
            "cream cheese", "half and half", "half-and-half", "whipping cream",
            "heavy cream", "buttermilk", "ghee", "mascarpone", "gruyere", "swiss"
        }),

        // Eggs
        ("Eggs", new[]
        {
            "egg", "eggs", "egg white", "egg yolk"
        }),

        // Meat - check after produce to avoid false positives
        ("Meat", new[]
        {
            "chicken", "beef", "pork", "lamb", "turkey", "bacon", "sausage", "ham",
            "steak", "ground beef", "ground turkey", "ground pork", "ribeye", "sirloin",
            "tenderloin", "brisket", "roast", "pork chop", "pork ribs", "beef ribs",
            "chicken wing", "chicken thigh", "chicken breast", "turkey breast",
            "drumstick", "meatball", "mince", "veal", "duck", "goose", "venison",
            "prosciutto", "pancetta", "chorizo", "salami", "pepperoni"
        }),

        // Seafood
        ("Seafood", new[]
        {
            "fish", "salmon", "tuna", "shrimp", "prawn", "lobster", "crab", "scallop",
            "cod", "halibut", "tilapia", "bass", "trout", "sardine", "anchovy",
            "clam", "mussel", "oyster", "squid", "calamari", "octopus", "mahi",
            "snapper", "swordfish", "mackerel"
        }),

        // Bread & Bakery
        ("Bread & Bakery", new[]
        {
            "bread", "baguette", "roll", "bun", "tortilla", "pita", "naan",
            "croissant", "bagel", "english muffin", "crouton", "breadcrumb",
            "panko", "pizza dough", "pie crust", "puff pastry", "phyllo"
        }),

        // Grains & Pasta
        ("Grains & Pasta", new[]
        {
            "rice", "pasta", "spaghetti", "penne", "fettuccine", "linguine", "macaroni",
            "noodle", "ramen", "udon", "rice noodle", "lasagna", "orzo", "couscous",
            "quinoa", "barley", "oat", "oatmeal", "farro", "bulgur", "polenta",
            "cornmeal", "flour", "all-purpose flour", "bread flour", "whole wheat"
        }),

        // Canned & Jarred
        ("Canned & Jarred", new[]
        {
            "canned tomato", "tomato paste", "tomato sauce", "diced tomato",
            "crushed tomato", "canned bean", "canned corn", "canned tuna",
            "coconut milk", "stock", "broth", "chicken broth", "beef broth",
            "vegetable broth", "pickle", "olive", "roasted pepper", "artichoke heart",
            "sun-dried tomato", "capers"
        }),

        // Condiments
        ("Condiments", new[]
        {
            "ketchup", "mustard", "mayonnaise", "mayo", "soy sauce", "worcestershire",
            "hot sauce", "sriracha", "bbq sauce", "barbecue sauce", "teriyaki",
            "salsa", "pesto", "hummus", "tahini", "fish sauce", "oyster sauce",
            "hoisin", "miso", "vinegar", "balsamic", "red wine vinegar",
            "apple cider vinegar", "rice vinegar"
        }),

        // Oils & Fats
        ("Oils & Fats", new[]
        {
            "oil", "olive oil", "vegetable oil", "canola oil", "coconut oil",
            "sesame oil", "avocado oil", "peanut oil", "cooking spray", "shortening",
            "lard"
        }),

        // Spices - check later since many ingredients might contain spice words
        ("Spices", new[]
        {
            "salt", "pepper", "black pepper", "white pepper", "paprika", "cumin",
            "coriander", "turmeric", "cinnamon", "nutmeg", "allspice", "clove",
            "cardamom", "ginger powder", "garlic powder", "onion powder",
            "chili powder", "cayenne", "red pepper flake", "crushed red pepper",
            "curry powder", "garam masala", "italian seasoning", "herbs de provence",
            "bay leaf", "dried oregano", "dried basil", "dried thyme", "dried rosemary",
            "mustard powder", "celery seed", "fennel seed", "caraway", "sumac",
            "za'atar", "old bay", "seasoning", "spice"
        }),

        // Baking
        ("Baking", new[]
        {
            "sugar", "brown sugar", "powdered sugar", "confectioner", "honey",
            "maple syrup", "molasses", "corn syrup", "agave", "vanilla", "vanilla extract",
            "baking powder", "baking soda", "yeast", "cocoa", "chocolate chip",
            "chocolate", "almond extract", "food coloring", "sprinkles", "gelatin"
        }),

        // Nuts & Seeds
        ("Nuts & Seeds", new[]
        {
            "almond", "walnut", "pecan", "cashew", "peanut", "pistachio", "hazelnut",
            "macadamia", "pine nut", "chestnut", "sunflower seed", "pumpkin seed",
            "sesame seed", "chia seed", "flax seed", "hemp seed", "nut butter",
            "peanut butter", "almond butter"
        }),

        // Frozen
        ("Frozen", new[]
        {
            "frozen vegetable", "frozen fruit", "frozen berry", "frozen pea",
            "frozen corn", "ice cream", "frozen pizza", "frozen dinner"
        }),

        // Beverages
        ("Beverages", new[]
        {
            "wine", "beer", "vodka", "rum", "whiskey", "bourbon", "tequila", "brandy",
            "sherry", "marsala", "vermouth", "sake", "mirin", "coffee", "espresso", "tea"
        })
    ];

    public string InferCategory(string ingredientName)
    {
        if (string.IsNullOrWhiteSpace(ingredientName))
            return CategoryDefaults.DefaultCategory;

        var normalized = ingredientName.ToLowerInvariant().Trim();

        // First pass: check for exact matches (highest confidence)
        foreach (var (category, keywords) in CategoryKeywords)
        {
            foreach (var keyword in keywords)
            {
                if (normalized.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        // Second pass: check for word boundary matches (e.g., "fresh broccoli" matches "broccoli")
        foreach (var (category, keywords) in CategoryKeywords)
        {
            foreach (var keyword in keywords)
            {
                // Check if keyword appears as a whole word (not substring of another word)
                if (IsWholeWordMatch(normalized, keyword))
                {
                    return category;
                }
            }
        }

        // Third pass: substring matching as fallback
        foreach (var (category, keywords) in CategoryKeywords)
        {
            foreach (var keyword in keywords)
            {
                if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        // Default fallback
        return CategoryDefaults.DefaultCategory;
    }

    private static bool IsWholeWordMatch(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        // Check character before (if any) is not a letter
        if (index > 0 && char.IsLetter(text[index - 1]))
            return false;

        // Check character after (if any) is not a letter
        var endIndex = index + word.Length;
        if (endIndex < text.Length && char.IsLetter(text[endIndex]))
            return false;

        return true;
    }
}
