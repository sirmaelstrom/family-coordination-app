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
    // Category mappings - order matters (more specific matches first)
    private static readonly Dictionary<string, string[]> CategoryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Meat"] = new[]
        {
            "chicken", "beef", "pork", "lamb", "turkey", "bacon", "sausage", "ham",
            "steak", "ground beef", "ground turkey", "ground pork", "ribeye", "sirloin",
            "tenderloin", "brisket", "roast", "chop", "ribs", "wing", "thigh", "breast",
            "drumstick", "meatball", "mince", "veal", "duck", "goose", "venison",
            "prosciutto", "pancetta", "chorizo", "salami", "pepperoni"
        },
        ["Seafood"] = new[]
        {
            "fish", "salmon", "tuna", "shrimp", "prawn", "lobster", "crab", "scallop",
            "cod", "halibut", "tilapia", "bass", "trout", "sardine", "anchovy",
            "clam", "mussel", "oyster", "squid", "calamari", "octopus", "mahi",
            "snapper", "swordfish", "mackerel"
        },
        ["Dairy"] = new[]
        {
            "milk", "cream", "butter", "cheese", "yogurt", "sour cream", "cottage cheese",
            "ricotta", "mozzarella", "cheddar", "parmesan", "feta", "gouda", "brie",
            "cream cheese", "half and half", "half-and-half", "whipping cream",
            "heavy cream", "buttermilk", "ghee", "mascarpone", "gruyere", "swiss"
        },
        ["Produce"] = new[]
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
        },
        ["Eggs"] = new[]
        {
            "egg", "eggs", "egg white", "egg yolk"
        },
        ["Bread & Bakery"] = new[]
        {
            "bread", "baguette", "roll", "bun", "tortilla", "pita", "naan",
            "croissant", "bagel", "english muffin", "crouton", "breadcrumb",
            "panko", "pizza dough", "pie crust", "puff pastry", "phyllo"
        },
        ["Grains & Pasta"] = new[]
        {
            "rice", "pasta", "spaghetti", "penne", "fettuccine", "linguine", "macaroni",
            "noodle", "ramen", "udon", "rice noodle", "lasagna", "orzo", "couscous",
            "quinoa", "barley", "oat", "oatmeal", "farro", "bulgur", "polenta",
            "cornmeal", "flour", "all-purpose flour", "bread flour", "whole wheat"
        },
        ["Canned & Jarred"] = new[]
        {
            "canned tomato", "tomato paste", "tomato sauce", "diced tomato",
            "crushed tomato", "canned bean", "canned corn", "canned tuna",
            "coconut milk", "stock", "broth", "chicken broth", "beef broth",
            "vegetable broth", "pickle", "olive", "roasted pepper", "artichoke heart",
            "sun-dried tomato", "capers"
        },
        ["Condiments"] = new[]
        {
            "ketchup", "mustard", "mayonnaise", "mayo", "soy sauce", "worcestershire",
            "hot sauce", "sriracha", "bbq sauce", "barbecue sauce", "teriyaki",
            "salsa", "pesto", "hummus", "tahini", "fish sauce", "oyster sauce",
            "hoisin", "miso", "vinegar", "balsamic", "red wine vinegar",
            "apple cider vinegar", "rice vinegar"
        },
        ["Oils & Fats"] = new[]
        {
            "oil", "olive oil", "vegetable oil", "canola oil", "coconut oil",
            "sesame oil", "avocado oil", "peanut oil", "cooking spray", "shortening",
            "lard"
        },
        ["Spices"] = new[]
        {
            "salt", "pepper", "black pepper", "white pepper", "paprika", "cumin",
            "coriander", "turmeric", "cinnamon", "nutmeg", "allspice", "clove",
            "cardamom", "ginger powder", "garlic powder", "onion powder",
            "chili powder", "cayenne", "red pepper flake", "crushed red pepper",
            "curry powder", "garam masala", "italian seasoning", "herbs de provence",
            "bay leaf", "dried oregano", "dried basil", "dried thyme", "dried rosemary",
            "mustard powder", "celery seed", "fennel seed", "caraway", "sumac",
            "za'atar", "old bay", "seasoning", "spice"
        },
        ["Baking"] = new[]
        {
            "sugar", "brown sugar", "powdered sugar", "confectioner", "honey",
            "maple syrup", "molasses", "corn syrup", "agave", "vanilla", "vanilla extract",
            "baking powder", "baking soda", "yeast", "cocoa", "chocolate chip",
            "chocolate", "almond extract", "food coloring", "sprinkles", "gelatin"
        },
        ["Nuts & Seeds"] = new[]
        {
            "almond", "walnut", "pecan", "cashew", "peanut", "pistachio", "hazelnut",
            "macadamia", "pine nut", "chestnut", "sunflower seed", "pumpkin seed",
            "sesame seed", "chia seed", "flax seed", "hemp seed", "nut butter",
            "peanut butter", "almond butter"
        },
        ["Frozen"] = new[]
        {
            "frozen vegetable", "frozen fruit", "frozen berry", "frozen pea",
            "frozen corn", "ice cream", "frozen pizza", "frozen dinner"
        },
        ["Beverages"] = new[]
        {
            "wine", "beer", "vodka", "rum", "whiskey", "bourbon", "tequila", "brandy",
            "sherry", "marsala", "vermouth", "sake", "mirin", "coffee", "espresso", "tea"
        }
    };

    public string InferCategory(string ingredientName)
    {
        if (string.IsNullOrWhiteSpace(ingredientName))
            return CategoryDefaults.DefaultCategory;

        var normalized = ingredientName.ToLowerInvariant().Trim();

        // Check each category's keywords
        foreach (var (category, keywords) in CategoryKeywords)
        {
            foreach (var keyword in keywords)
            {
                // Check for exact match or word boundary match
                if (normalized.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        // Default fallback
        return CategoryDefaults.DefaultCategory;
    }
}
