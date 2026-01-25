using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AllowNullableRecipeCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the existing FK constraint (which has Restrict behavior)
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_Users_CreatedByUserId",
                table: "Recipes");

            // Make CreatedByUserId nullable
            migrationBuilder.AlterColumn<int>(
                name: "CreatedByUserId",
                table: "Recipes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // Re-add the FK constraint with SetNull behavior
            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_Users_CreatedByUserId",
                table: "Recipes",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the SetNull FK constraint
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_Users_CreatedByUserId",
                table: "Recipes");

            // Update any NULL values back to a valid user (first user in household)
            // This is necessary before making the column non-nullable
            migrationBuilder.Sql(@"
                UPDATE ""Recipes"" r
                SET ""CreatedByUserId"" = (
                    SELECT u.""Id"" FROM ""Users"" u 
                    WHERE u.""HouseholdId"" = r.""HouseholdId"" 
                    ORDER BY u.""Id"" LIMIT 1
                )
                WHERE r.""CreatedByUserId"" IS NULL;
            ");

            // Make CreatedByUserId non-nullable again
            migrationBuilder.AlterColumn<int>(
                name: "CreatedByUserId",
                table: "Recipes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // Re-add the FK constraint with Restrict behavior
            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_Users_CreatedByUserId",
                table: "Recipes",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
