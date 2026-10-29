using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLibrarySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "LibraryGames",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ShareId",
                table: "LibraryGames",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            // Existing entries each need their own share id before the unique index goes on
            migrationBuilder.Sql("UPDATE LibraryGames SET ShareId = UUID();");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryGames_ShareId",
                table: "LibraryGames",
                column: "ShareId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LibraryGames_ShareId",
                table: "LibraryGames");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "LibraryGames");

            migrationBuilder.DropColumn(
                name: "ShareId",
                table: "LibraryGames");
        }
    }
}
