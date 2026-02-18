using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDenormalizedGameSessionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BoardSize",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MoveCount",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartTimeMinutes",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "TimeControlEnabled",
                table: "GameSessions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TimeIncrementSeconds",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoardSize",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "MoveCount",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "StartTimeMinutes",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "TimeControlEnabled",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "TimeIncrementSeconds",
                table: "GameSessions");
        }
    }
}
