using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdRecipeSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShoppingLists_HouseholdId_IsFavorite",
                table: "ShoppingLists");

            migrationBuilder.AlterColumn<bool>(
                name: "IsFavorite",
                table: "ShoppingLists",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SharedFromHouseholdId",
                table: "Recipes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFromHouseholdName",
                table: "Recipes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFromRecipeId",
                table: "Recipes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdConnections",
                columns: table => new
                {
                    HouseholdId1 = table.Column<int>(type: "integer", nullable: false),
                    HouseholdId2 = table.Column<int>(type: "integer", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitiatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    AcceptedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdConnections", x => new { x.HouseholdId1, x.HouseholdId2 });
                    table.CheckConstraint("CK_HouseholdConnections_OrderedIds", "\"HouseholdId1\" < \"HouseholdId2\"");
                    table.ForeignKey(
                        name: "FK_HouseholdConnections_Households_HouseholdId1",
                        column: x => x.HouseholdId1,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdConnections_Households_HouseholdId2",
                        column: x => x.HouseholdId2,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdConnections_Users_AcceptedByUserId",
                        column: x => x.AcceptedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HouseholdConnections_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    InviteCode = table.Column<string>(type: "character(6)", fixedLength: true, maxLength: 6, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedByHouseholdId = table.Column<int>(type: "integer", nullable: true),
                    UsedByUserId = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdInvites_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdInvites_Households_UsedByHouseholdId",
                        column: x => x.UsedByHouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HouseholdInvites_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdInvites_Users_UsedByUserId",
                        column: x => x.UsedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_HouseholdId_SharedFromHouseholdId",
                table: "Recipes",
                columns: new[] { "HouseholdId", "SharedFromHouseholdId" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_SharedFromHouseholdId",
                table: "Recipes",
                column: "SharedFromHouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdConnections_AcceptedByUserId",
                table: "HouseholdConnections",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdConnections_HouseholdId1",
                table: "HouseholdConnections",
                column: "HouseholdId1");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdConnections_HouseholdId2",
                table: "HouseholdConnections",
                column: "HouseholdId2");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdConnections_InitiatedByUserId",
                table: "HouseholdConnections",
                column: "InitiatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_CreatedByUserId",
                table: "HouseholdInvites",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_HouseholdId_IsUsed_ExpiresAt",
                table: "HouseholdInvites",
                columns: new[] { "HouseholdId", "IsUsed", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_InviteCode",
                table: "HouseholdInvites",
                column: "InviteCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_UsedByHouseholdId",
                table: "HouseholdInvites",
                column: "UsedByHouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_UsedByUserId",
                table: "HouseholdInvites",
                column: "UsedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_Households_SharedFromHouseholdId",
                table: "Recipes",
                column: "SharedFromHouseholdId",
                principalTable: "Households",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_Households_SharedFromHouseholdId",
                table: "Recipes");

            migrationBuilder.DropTable(
                name: "HouseholdConnections");

            migrationBuilder.DropTable(
                name: "HouseholdInvites");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_HouseholdId_SharedFromHouseholdId",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_SharedFromHouseholdId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SharedFromHouseholdId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SharedFromHouseholdName",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SharedFromRecipeId",
                table: "Recipes");

            migrationBuilder.AlterColumn<bool>(
                name: "IsFavorite",
                table: "ShoppingLists",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingLists_HouseholdId_IsFavorite",
                table: "ShoppingLists",
                columns: new[] { "HouseholdId", "IsFavorite" });
        }
    }
}
