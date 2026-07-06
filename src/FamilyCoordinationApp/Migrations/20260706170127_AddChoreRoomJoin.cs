using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyCoordinationApp.Migrations
{
    /// <inheritdoc />
    public partial class AddChoreRoomJoin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChoreRooms",
                columns: table => new
                {
                    HouseholdId = table.Column<int>(type: "integer", nullable: false),
                    ChoreId = table.Column<int>(type: "integer", nullable: false),
                    RoomId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoreRooms", x => new { x.HouseholdId, x.ChoreId, x.RoomId });
                    table.ForeignKey(
                        name: "FK_ChoreRooms_Chores_HouseholdId_ChoreId",
                        columns: x => new { x.HouseholdId, x.ChoreId },
                        principalTable: "Chores",
                        principalColumns: new[] { "HouseholdId", "ChoreId" });
                    table.ForeignKey(
                        name: "FK_ChoreRooms_Rooms_HouseholdId_RoomId",
                        columns: x => new { x.HouseholdId, x.RoomId },
                        principalTable: "Rooms",
                        principalColumns: new[] { "HouseholdId", "RoomId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreRooms_HouseholdId_RoomId",
                table: "ChoreRooms",
                columns: new[] { "HouseholdId", "RoomId" });

            // Backfill: one membership row per existing single-room chore (the expand step, M5).
            // Chore.RoomId is NOT dropped here — the shim stays until WP-08's contract migration.
            migrationBuilder.Sql(
                "INSERT INTO \"ChoreRooms\" (\"HouseholdId\", \"ChoreId\", \"RoomId\") " +
                "SELECT \"HouseholdId\", \"ChoreId\", \"RoomId\" FROM \"Chores\" WHERE \"RoomId\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChoreRooms");
        }
    }
}
