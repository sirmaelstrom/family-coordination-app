using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCollaborationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Initials",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PictureUrl",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ShoppingListItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "ShoppingListItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ShoppingListItems",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "Recipes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Recipes",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "MealPlanEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatedByUserId",
                table: "MealPlanEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "MealPlanEntries",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItems_UpdatedByUserId",
                table: "ShoppingListItems",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_UpdatedByUserId",
                table: "Recipes",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_UpdatedByUserId",
                table: "MealPlanEntries",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntries_Users_UpdatedByUserId",
                table: "MealPlanEntries",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_Users_UpdatedByUserId",
                table: "Recipes",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItems_Users_UpdatedByUserId",
                table: "ShoppingListItems",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntries_Users_UpdatedByUserId",
                table: "MealPlanEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_Users_UpdatedByUserId",
                table: "Recipes");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItems_Users_UpdatedByUserId",
                table: "ShoppingListItems");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItems_UpdatedByUserId",
                table: "ShoppingListItems");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_UpdatedByUserId",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_MealPlanEntries_UpdatedByUserId",
                table: "MealPlanEntries");

            migrationBuilder.DropColumn(
                name: "Initials",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PictureUrl",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "MealPlanEntries");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "MealPlanEntries");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "MealPlanEntries");
        }
    }
}
