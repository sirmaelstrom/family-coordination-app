using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberCalendarLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HouseholdCalendarTokens_HouseholdId",
                table: "HouseholdCalendarTokens");

            migrationBuilder.AddColumn<string>(
                name: "Feed",
                table: "HouseholdCalendarTokens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MealPlan");

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "HouseholdCalendarTokens",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdCalendarTokens_Active_HouseholdId_UserId_Feed",
                table: "HouseholdCalendarTokens",
                columns: new[] { "HouseholdId", "UserId", "Feed" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdCalendarTokens_UserId",
                table: "HouseholdCalendarTokens",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdCalendarTokens_Users_UserId",
                table: "HouseholdCalendarTokens",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdCalendarTokens_Users_UserId",
                table: "HouseholdCalendarTokens");

            migrationBuilder.DropIndex(
                name: "IX_HouseholdCalendarTokens_Active_HouseholdId_UserId_Feed",
                table: "HouseholdCalendarTokens");

            migrationBuilder.DropIndex(
                name: "IX_HouseholdCalendarTokens_UserId",
                table: "HouseholdCalendarTokens");

            migrationBuilder.DropColumn(
                name: "Feed",
                table: "HouseholdCalendarTokens");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "HouseholdCalendarTokens");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdCalendarTokens_HouseholdId",
                table: "HouseholdCalendarTokens",
                column: "HouseholdId",
                unique: true,
                filter: "\"RevokedAt\" IS NULL");
        }
    }
}
