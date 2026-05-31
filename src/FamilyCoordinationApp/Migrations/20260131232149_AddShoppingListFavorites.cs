using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddShoppingListFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent raw SQL (instead of AddColumn/CreateIndex builder calls).
            // This migration originally shipped WITHOUT a .Designer.cs, so EF never
            // discovered it and skipped it on fresh DBs. It is now discoverable again
            // (Designer added), which means on already-migrated production DBs EF will
            // see it as pending and run this Up() out of order AFTER
            // 20260207043951_AddHouseholdRecipeSharing has already added the IsFavorite
            // column (and dropped this index). The IF NOT EXISTS guards make that
            // re-application a safe no-op. On a fresh DB this runs in order and creates
            // the column + index; AddHouseholdRecipeSharing then drops the index.
            migrationBuilder.Sql("ALTER TABLE \"ShoppingLists\" ADD COLUMN IF NOT EXISTS \"IsFavorite\" boolean NOT NULL DEFAULT false;");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_ShoppingLists_HouseholdId_IsFavorite\" ON \"ShoppingLists\" (\"HouseholdId\", \"IsFavorite\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ShoppingLists_HouseholdId_IsFavorite\";");

            migrationBuilder.Sql("ALTER TABLE \"ShoppingLists\" DROP COLUMN IF EXISTS \"IsFavorite\";");
        }
    }
}
