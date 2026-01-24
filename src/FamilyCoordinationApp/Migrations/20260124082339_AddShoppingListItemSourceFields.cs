using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddShoppingListItemSourceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsManuallyAdded",
                table: "ShoppingListItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OriginalUnits",
                table: "ShoppingListItems",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuantityDelta",
                table: "ShoppingListItems",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeIngredientIds",
                table: "ShoppingListItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ShoppingListItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceRecipes",
                table: "ShoppingListItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsManuallyAdded",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "OriginalUnits",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "QuantityDelta",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "RecipeIngredientIds",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "SourceRecipes",
                table: "ShoppingListItems");
        }
    }
}
