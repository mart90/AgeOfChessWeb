using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricGameDenormalizedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BoardSize",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MoveCount",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StartTimeMinutes",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "TimeControlEnabled",
                table: "HistoricGames",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TimeIncrementSeconds",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoardSize",
                table: "HistoricGames");

            migrationBuilder.DropColumn(
                name: "MoveCount",
                table: "HistoricGames");

            migrationBuilder.DropColumn(
                name: "StartTimeMinutes",
                table: "HistoricGames");

            migrationBuilder.DropColumn(
                name: "TimeControlEnabled",
                table: "HistoricGames");

            migrationBuilder.DropColumn(
                name: "TimeIncrementSeconds",
                table: "HistoricGames");
        }
    }
}
