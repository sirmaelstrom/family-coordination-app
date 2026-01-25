namespace FamilyCoordinationApp.Services;

/// <summary>
/// Service that automatically categorizes ingredients based on keyword matching.
/// Maps common ingredient names to grocery store categories.
/// </summary>
public interface IIngredientCategorizationService
{
    /// <summary>
    /// Returns the most appropriate category for the given ingredient name.
    /// Falls back to "Pantry" if no match is found.
    /// </summary>
    string CategorizeIngredient(string ingredientName);
}

public class IngredientCategorizationService : IIngredientCategorizationService
{
    // Category mappings ordered by specificity (more specific patterns first)
    private static readonly Dictionary<string, string[]> CategoryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Produce - Fruits & Vegetables
        ["Produce"] = [
            // Vegetables
            "lettuce", "spinach", "kale", "arugula", "cabbage", "broccoli", "cauliflower",
            "carrot", "carrots", "celery", "cucumber", "tomato", "tomatoes", "onion", "onions",
            "garlic", "ginger", "potato", "potatoes", "sweet potato", "yam", "zucchini",
            "squash", "eggplant", "bell pepper", "pepper", "peppers", "jalapeño", "jalapeno",
            "mushroom", "mushrooms", "asparagus", "green bean", "green beans", "pea", "peas",
            "corn", "artichoke", "beet", "beets", "radish", "turnip", "parsnip",
            "leek", "scallion", "shallot", "chive", "bok choy", "brussels sprout",
            // Fruits
            "apple", "apples", "banana", "bananas", "orange", "oranges", "lemon", "lemons",
            "lime", "limes", "grapefruit", "grape", "grapes", "strawberry", "strawberries",
            "blueberry", "blueberries", "raspberry", "raspberries", "blackberry", "blackberries",
            "cherry", "cherries", "peach", "peaches", "plum", "plums", "pear", "pears",
            "mango", "mangoes", "pineapple", "watermelon", "cantaloupe", "honeydew",
            "kiwi", "papaya", "avocado", "coconut", "pomegranate", "fig", "date", "dates",
            // Fresh herbs (often in produce section)
            "fresh basil", "fresh cilantro", "fresh parsley", "fresh mint", "fresh dill",
            "fresh rosemary", "fresh thyme", "fresh oregano", "fresh sage", "fresh chives"
        ],

        // Dairy & Eggs
        ["Dairy"] = [
            "milk", "cream", "half and half", "half-and-half", "heavy cream", "whipping cream",
            "sour cream", "yogurt", "greek yogurt", "butter", "margarine",
            "cheese", "cheddar", "mozzarella", "parmesan", "feta", "ricotta", "cream cheese",
            "cottage cheese", "brie", "gouda", "swiss", "provolone", "jack cheese",
            "egg", "eggs", "egg white", "egg yolk"
        ],

        // Meat & Seafood
        ["Meat"] = [
            // Beef
            "beef", "steak", "ground beef", "sirloin", "ribeye", "chuck", "brisket",
            "roast", "beef roast", "filet", "tenderloin",
            // Pork
            "pork", "bacon", "ham", "sausage", "pork chop", "pork loin", "pork tenderloin",
            "ground pork", "prosciutto", "pancetta", "chorizo",
            // Poultry
            "chicken", "chicken breast", "chicken thigh", "chicken wing", "ground chicken",
            "turkey", "ground turkey", "turkey breast", "duck",
            // Lamb
            "lamb", "lamb chop", "ground lamb",
            // Seafood
            "fish", "salmon", "tuna", "cod", "tilapia", "halibut", "trout", "sea bass",
            "shrimp", "prawns", "crab", "lobster", "scallop", "scallops", "clam", "clams",
            "mussel", "mussels", "oyster", "oysters", "squid", "calamari", "octopus",
            "anchovy", "anchovies", "sardine", "sardines"
        ],

        // Bakery & Bread
        ["Bakery"] = [
            "bread", "loaf", "baguette", "ciabatta", "sourdough", "brioche",
            "roll", "rolls", "bun", "buns", "bagel", "bagels", "croissant", "croissants",
            "english muffin", "pita", "naan", "tortilla", "tortillas", "wrap", "wraps",
            "breadcrumb", "breadcrumbs", "panko", "crouton", "croutons"
        ],

        // Frozen Foods
        ["Frozen"] = [
            "frozen", "ice cream", "frozen yogurt", "sorbet", "gelato",
            "frozen vegetable", "frozen fruit", "frozen pizza", "frozen dinner",
            "frozen fries", "frozen peas", "frozen corn", "frozen berries"
        ],

        // Canned Goods
        ["Canned"] = [
            "canned", "can of", "canned tomato", "tomato sauce", "tomato paste",
            "canned bean", "canned chickpea", "canned corn", "canned tuna",
            "coconut milk", "evaporated milk", "condensed milk", "broth", "stock",
            "chicken broth", "beef broth", "vegetable broth", "chicken stock", "beef stock"
        ],

        // Condiments & Sauces
        ["Condiments"] = [
            "ketchup", "mustard", "mayonnaise", "mayo", "relish", "hot sauce",
            "soy sauce", "teriyaki", "worcestershire", "bbq sauce", "barbecue sauce",
            "salsa", "sriracha", "tabasco", "fish sauce", "oyster sauce", "hoisin",
            "vinegar", "balsamic", "red wine vinegar", "apple cider vinegar",
            "salad dressing", "ranch", "italian dressing", "vinaigrette",
            "peanut butter", "almond butter", "jam", "jelly", "preserves", "honey", "maple syrup",
            "olive oil", "vegetable oil", "canola oil", "sesame oil", "coconut oil"
        ],

        // Spices & Seasonings
        ["Spices"] = [
            "salt", "pepper", "black pepper", "white pepper", "cayenne",
            "paprika", "smoked paprika", "cumin", "coriander", "turmeric", "curry",
            "cinnamon", "nutmeg", "clove", "cloves", "allspice", "cardamom", "ginger powder",
            "oregano", "basil", "thyme", "rosemary", "sage", "dill", "parsley", "cilantro",
            "bay leaf", "bay leaves", "chili powder", "chili flake", "red pepper flake",
            "garlic powder", "onion powder", "italian seasoning", "herbs de provence",
            "vanilla", "vanilla extract", "almond extract", "mint extract"
        ],

        // Grains & Pasta
        ["Grains"] = [
            "rice", "white rice", "brown rice", "jasmine rice", "basmati", "arborio",
            "pasta", "spaghetti", "penne", "fettuccine", "linguine", "rigatoni", "macaroni",
            "lasagna", "orzo", "couscous", "quinoa", "barley", "farro", "bulgur",
            "oat", "oats", "oatmeal", "rolled oats", "steel cut oats",
            "flour", "all-purpose flour", "bread flour", "whole wheat flour", "almond flour",
            "cornmeal", "polenta", "grits", "cereal", "granola"
        ],

        // Baking
        ["Baking"] = [
            "sugar", "brown sugar", "powdered sugar", "confectioner", "granulated sugar",
            "baking soda", "baking powder", "yeast", "active dry yeast", "instant yeast",
            "cocoa", "cocoa powder", "chocolate chip", "chocolate", "dark chocolate",
            "cornstarch", "cream of tartar", "gelatin", "pectin",
            "molasses", "corn syrup", "agave", "stevia",
            "sprinkles", "food coloring"
        ],

        // Snacks
        ["Snacks"] = [
            "chip", "chips", "potato chips", "tortilla chips", "crackers", "pretzel", "pretzels",
            "popcorn", "nuts", "almonds", "peanuts", "cashews", "walnuts", "pecans", "pistachios",
            "trail mix", "granola bar", "protein bar", "dried fruit", "raisins", "cranberries",
            "cookie", "cookies", "candy"
        ],

        // Beverages
        ["Beverages"] = [
            "juice", "orange juice", "apple juice", "grape juice", "cranberry juice",
            "soda", "cola", "sprite", "ginger ale", "tonic water", "club soda",
            "coffee", "espresso", "tea", "green tea", "black tea", "herbal tea",
            "wine", "red wine", "white wine", "beer", "vodka", "rum", "whiskey", "tequila",
            "sparkling water", "mineral water", "coconut water", "almond milk", "oat milk", "soy milk"
        ]
    };

    // Exact match overrides (for ambiguous terms)
    private static readonly Dictionary<string, string> ExactMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fresh herbs go to Produce, dried herbs go to Spices
        ["basil"] = "Spices",
        ["cilantro"] = "Produce",
        ["parsley"] = "Produce",
        ["mint"] = "Produce",
        ["dill"] = "Spices",
        ["rosemary"] = "Spices",
        ["thyme"] = "Spices",
        ["oregano"] = "Spices",
        ["sage"] = "Spices",
        // Common items
        ["oil"] = "Condiments",
        ["sugar"] = "Baking",
        ["flour"] = "Grains",
        ["salt"] = "Spices",
        ["pepper"] = "Spices",
        ["water"] = "Beverages"
    };

    public string CategorizeIngredient(string ingredientName)
    {
        if (string.IsNullOrWhiteSpace(ingredientName))
            return "Pantry";

        var name = ingredientName.ToLowerInvariant().Trim();

        // Check exact matches first
        if (ExactMatches.TryGetValue(name, out var exactCategory))
            return exactCategory;

        // Check each category's keywords
        foreach (var (category, keywords) in CategoryKeywords)
        {
            foreach (var keyword in keywords)
            {
                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return category;
            }
        }

        // Default fallback
        return "Pantry";
    }
}
