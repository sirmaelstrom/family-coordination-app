using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class DropChoreRoomId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chores_Rooms_HouseholdId_RoomId",
                table: "Chores");

            migrationBuilder.DropIndex(
                name: "IX_Chores_HouseholdId_RoomId",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "Chores");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoomId",
                table: "Chores",
                type: "integer",
                nullable: true);

            // Best-effort / LOSSY down-migration: restore the shim from the MIN membership per chore. A
            // multi-room chore collapses to a single room (its other memberships are not representable on the
            // single column) — acceptable because this is a rollback path, not a production forward path. The
            // FK below then holds (every restored RoomId is a real room). Roomless chores stay null (General).
            migrationBuilder.Sql(
                "UPDATE \"Chores\" c SET \"RoomId\" = sub.min_room " +
                "FROM (SELECT \"HouseholdId\", \"ChoreId\", MIN(\"RoomId\") AS min_room " +
                "FROM \"ChoreRooms\" GROUP BY \"HouseholdId\", \"ChoreId\") sub " +
                "WHERE c.\"HouseholdId\" = sub.\"HouseholdId\" AND c.\"ChoreId\" = sub.\"ChoreId\";");

            migrationBuilder.CreateIndex(
                name: "IX_Chores_HouseholdId_RoomId",
                table: "Chores",
                columns: new[] { "HouseholdId", "RoomId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Chores_Rooms_HouseholdId_RoomId",
                table: "Chores",
                columns: new[] { "HouseholdId", "RoomId" },
                principalTable: "Rooms",
                principalColumns: new[] { "HouseholdId", "RoomId" });
        }
    }
}
