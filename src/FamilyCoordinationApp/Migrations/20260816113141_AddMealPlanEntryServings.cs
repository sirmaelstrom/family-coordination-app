using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMealPlanEntryServings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Servings",
                table: "MealPlanEntries",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Servings",
                table: "MealPlanEntries");
        }
    }
}
