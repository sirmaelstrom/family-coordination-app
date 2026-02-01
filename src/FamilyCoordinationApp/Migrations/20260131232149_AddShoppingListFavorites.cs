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
            migrationBuilder.AddColumn<bool>(
                name: "IsFavorite",
                table: "ShoppingLists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingLists_HouseholdId_IsFavorite",
                table: "ShoppingLists",
                columns: new[] { "HouseholdId", "IsFavorite" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShoppingLists_HouseholdId_IsFavorite",
                table: "ShoppingLists");

            migrationBuilder.DropColumn(
                name: "IsFavorite",
                table: "ShoppingLists");
        }
    }
}
