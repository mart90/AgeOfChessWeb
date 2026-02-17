using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGameEloSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BlackEloAtGame",
                table: "GameSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlackEloDelta",
                table: "GameSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhiteEloAtGame",
                table: "GameSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhiteEloDelta",
                table: "GameSessions",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlackEloAtGame",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "BlackEloDelta",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "WhiteEloAtGame",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "WhiteEloDelta",
                table: "GameSessions");
        }
    }
}
