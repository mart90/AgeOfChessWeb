using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class HistoricGamePlayerIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HistoricGames_BlackPlayerId",
                table: "HistoricGames",
                column: "BlackPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricGames_WhitePlayerId",
                table: "HistoricGames",
                column: "WhitePlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoricGames_BlackPlayerId",
                table: "HistoricGames");

            migrationBuilder.DropIndex(
                name: "IX_HistoricGames_WhitePlayerId",
                table: "HistoricGames");
        }
    }
}
