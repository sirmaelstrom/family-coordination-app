using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddChoreSnoozeEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChoreSnoozeEvents",
                columns: table => new
                {
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    ChoreId = table.Column<int>(type: "integer", nullable: false),
                    SnoozeEventId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SetByUserId = table.Column<int>(type: "integer", nullable: false),
                    SetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SnoozedUntil = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoreSnoozeEvents", x => new { x.HouseholdId, x.ChoreId, x.SnoozeEventId });
                    table.ForeignKey(
                        name: "FK_ChoreSnoozeEvents_Chores_HouseholdId_ChoreId",
                        columns: x => new { x.HouseholdId, x.ChoreId },
                        principalTable: "Chores",
                        principalColumns: new[] { "HouseholdId", "ChoreId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChoreSnoozeEvents_Users_SetByUserId",
                        column: x => x.SetByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreSnoozeEvents_SetByUserId",
                table: "ChoreSnoozeEvents",
                column: "SetByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChoreSnoozeEvents");
        }
    }
}
