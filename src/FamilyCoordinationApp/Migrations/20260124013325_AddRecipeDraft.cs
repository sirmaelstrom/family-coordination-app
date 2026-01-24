using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeDrafts",
                columns: table => new
                {
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RecipeId = table.Column<int>(type: "integer", nullable: false),
                    DraftJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeDrafts", x => new { x.HouseholdId, x.UserId, x.RecipeId });
                    table.ForeignKey(
                        name: "FK_RecipeDrafts_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeDrafts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_HouseholdId_Name",
                table: "Recipes",
                columns: new[] { "HouseholdId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeDrafts_UserId",
                table: "RecipeDrafts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeDrafts");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_HouseholdId_Name",
                table: "Recipes");
        }
    }
}
