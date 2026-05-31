using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent raw SQL. This migration originally shipped without a
            // .Designer.cs, so EF never discovered it. With its Designer now restored
            // it is discoverable; the IF NOT EXISTS guard keeps re-application safe on
            // any DB where the column was already created out-of-band.
            migrationBuilder.Sql("ALTER TABLE \"Recipes\" ADD COLUMN IF NOT EXISTS \"RecipeType\" integer NOT NULL DEFAULT 0;");  // 0 = Main
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Recipes\" DROP COLUMN IF EXISTS \"RecipeType\";");
        }
    }
}
