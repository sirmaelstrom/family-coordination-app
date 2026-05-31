using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations;

/// <inheritdoc />
public partial class AddUserFavorites : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Idempotent raw SQL. This migration originally shipped without a .Designer.cs,
        // so EF never discovered it. With its Designer now restored it is discoverable;
        // the IF NOT EXISTS guards keep re-application safe on any DB where the table
        // was already created out-of-band. Mirrors the DDL the CreateTable/CreateIndex
        // builder calls emitted (composite PK, two cascade FKs, the HouseholdId/RecipeId
        // index).
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""UserFavorites"" (
                ""UserId"" integer NOT NULL,
                ""HouseholdId"" integer NOT NULL,
                ""RecipeId"" integer NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_UserFavorites"" PRIMARY KEY (""UserId"", ""HouseholdId"", ""RecipeId""),
                CONSTRAINT ""FK_UserFavorites_Recipes_HouseholdId_RecipeId"" FOREIGN KEY (""HouseholdId"", ""RecipeId"") REFERENCES ""Recipes"" (""HouseholdId"", ""RecipeId"") ON DELETE CASCADE,
                CONSTRAINT ""FK_UserFavorites_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
            );");

        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_UserFavorites_HouseholdId_RecipeId\" ON \"UserFavorites\" (\"HouseholdId\", \"RecipeId\");");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"UserFavorites\";");
    }
}
