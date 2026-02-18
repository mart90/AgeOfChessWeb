using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSlowGamePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BlackStartingGold",
                table: "GameSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlackTimeMsRemaining",
                table: "GameSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhiteTimeMsRemaining",
                table: "GameSessions",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlackStartingGold",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "BlackTimeMsRemaining",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "WhiteTimeMsRemaining",
                table: "GameSessions");
        }
    }
}
