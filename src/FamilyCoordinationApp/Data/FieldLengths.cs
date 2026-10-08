namespace FamilyCoordinationApp.Data;

/// <summary>
/// The string column limits, one constant per <c>HasMaxLength</c> in <c>Data/Configurations</c> (quest ec7a7331).
/// The EF configurations declare the column from these, and the <c>/api</c> request records validate against the
/// same constants (<c>[MaxTextLength(FieldLengths.X.Y)]</c>), so a limit cannot drift between the column and the
/// check. Changing a value here changes the column: it needs a migration.
/// </summary>
public static class FieldLengths
{
    public static class Category
    {
        public const int Name = 50;
        public const int IconEmoji = 30; // emoji shortcode names like "cup_with_straw"
        public const int Color = 7;      // #FFFFFF format
    }

    public static class Chore
    {
        public const int Name = 200;
        public const int Description = 2000;
        public const int Icon = 30;
        public const int RecurrenceMode = 20;
        public const int DaysOfWeek = 60;
        public const int EffortTier = 20;
        public const int Status = 20;
        public const int AssignmentKind = 20;
        public const int PhotoPath = 500;
    }

    public static class ChoreCompletion
    {
        public const int Note = 2000;
        public const int PhotoPath = 500;
    }

    public static class ChoreEvent
    {
        public const int Type = 20;
    }

    public static class ChoreParticipationEvent
    {
        public const int Type = 20;
    }

    public static class ChoreSubtask
    {
        public const int Title = 200;
    }

    public static class Feedback
    {
        public const int Message = 4000;
        public const int CurrentPage = 500;
        public const int UserAgent = 500;
        public const int AdminNotes = 2000;
    }

    public static class HouseholdCalendarToken
    {
        public const int TokenHash = 64;
    }

    public static class HouseholdChoreDigestSettings
    {
        public const int WebhookUrlProtected = 2000;
        public const int Cadence = 20;
    }

    public static class Household
    {
        public const int Name = 200;
    }

    public static class HouseholdInvite
    {
        public const int InviteCode = 6;
    }

    public static class HouseholdRequest
    {
        public const int Email = 256;
        public const int DisplayName = 200;
        public const int GoogleId = 200;
        public const int HouseholdName = 200;
        public const int Status = 50;
        public const int ReviewedBy = 256;
        public const int RejectionReason = 500;
    }

    public static class MealPlanEntry
    {
        public const int CustomMealName = 200;
        public const int Notes = 500;
    }

    public static class Recipe
    {
        public const int Name = 200;
        public const int Description = 1000;
        public const int Instructions = 10000;
        public const int ImagePath = 500;
        public const int SourceUrl = 2000;
        public const int SharedFromHouseholdName = 200;
    }

    public static class RecipeIngredient
    {
        public const int Name = 200;
        public const int Unit = 50;
        public const int Category = 50;
    }

    public static class Room
    {
        public const int Name = 100;
        public const int Icon = 30;
        public const int PhotoPath = 500;
    }

    public static class ShoppingList
    {
        public const int Name = 200;
    }

    public static class ShoppingListItem
    {
        public const int Name = 200;
        public const int Unit = 50;
        public const int Category = 50;
        public const int SourceRecipes = 500;
        public const int OriginalUnits = 200;
        public const int RecipeIngredientIds = 500;
    }

    public static class User
    {
        public const int Email = 256;
        public const int DisplayName = 200;
        public const int GoogleId = 200;
    }
}
