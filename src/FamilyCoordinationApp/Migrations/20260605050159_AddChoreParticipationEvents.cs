using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddChoreParticipationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChoreParticipationEvents",
                columns: table => new
                {
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    ChoreId = table.Column<int>(type: "integer", nullable: false),
                    ParticipationEventId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubjectUserId = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoreParticipationEvents", x => new { x.HouseholdId, x.ChoreId, x.ParticipationEventId });
                    table.ForeignKey(
                        name: "FK_ChoreParticipationEvents_Chores_HouseholdId_ChoreId",
                        columns: x => new { x.HouseholdId, x.ChoreId },
                        principalTable: "Chores",
                        principalColumns: new[] { "HouseholdId", "ChoreId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChoreParticipationEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChoreParticipationEvents_Users_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreParticipationEvents_ActorUserId",
                table: "ChoreParticipationEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChoreParticipationEvents_HouseholdId_ChoreId_At",
                table: "ChoreParticipationEvents",
                columns: new[] { "HouseholdId", "ChoreId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreParticipationEvents_SubjectUserId",
                table: "ChoreParticipationEvents",
                column: "SubjectUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChoreParticipationEvents");
        }
    }
}
