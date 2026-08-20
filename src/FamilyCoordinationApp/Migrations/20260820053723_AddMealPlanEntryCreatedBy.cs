using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMealPlanEntryCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "MealPlanEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_CreatedByUserId",
                table: "MealPlanEntries",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntries_Users_CreatedByUserId",
                table: "MealPlanEntries",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntries_Users_CreatedByUserId",
                table: "MealPlanEntries");

            migrationBuilder.DropIndex(
                name: "IX_MealPlanEntries_CreatedByUserId",
                table: "MealPlanEntries");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "MealPlanEntries");
        }
    }
}
