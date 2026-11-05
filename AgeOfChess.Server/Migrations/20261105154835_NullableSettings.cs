using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <inheritdoc />
    public partial class NullableSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "TimeIncrementSeconds",
                table: "HistoricGames",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "StartTimeMinutes",
                table: "HistoricGames",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "HandicapGold",
                table: "HistoricGames",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "CreatorColor",
                table: "HistoricGames",
                type: "varchar(6)",
                maxLength: 6,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(6)",
                oldMaxLength: 6)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<bool>(
                name: "ComputerIsWhite",
                table: "HistoricGames",
                type: "tinyint(1)",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)");

            migrationBuilder.AlterColumn<int>(
                name: "TimeIncrementSeconds",
                table: "GameSessions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "StartTimeMinutes",
                table: "GameSessions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "HandicapGold",
                table: "GameSessions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "CreatorColor",
                table: "GameSessions",
                type: "varchar(6)",
                maxLength: 6,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(6)",
                oldMaxLength: 6)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<bool>(
                name: "ComputerIsWhite",
                table: "GameSessions",
                type: "tinyint(1)",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)");

            // Options that don't apply to a game are null (GameSettings.Normalize). CreatorColor
            // "white" was also the default for matchmaking games and rematches, so it's dropped
            // everywhere: only "black" / "random" were certainly chosen by a lobby creator.
            foreach (var table in new[] { "GameSessions", "HistoricGames" })
                migrationBuilder.Sql($@"
                    UPDATE `{table}` SET
                        StartTimeMinutes     = IF(TimeControlEnabled, StartTimeMinutes, NULL),
                        TimeIncrementSeconds = IF(TimeControlEnabled, TimeIncrementSeconds, NULL),
                        ComputerIsWhite      = IF(ComputerLevel IS NULL, NULL, ComputerIsWhite),
                        CreatorColor         = IF(ComputerLevel IS NOT NULL OR BiddingEnabled OR CreatorColor = 'white', NULL, CreatorColor),
                        HandicapGold         = IF(Handicap = 'manual', HandicapGold, NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "GameSessions", "HistoricGames" })
                migrationBuilder.Sql($@"
                    UPDATE `{table}` SET
                        StartTimeMinutes     = COALESCE(StartTimeMinutes, 0),
                        TimeIncrementSeconds = COALESCE(TimeIncrementSeconds, 0),
                        ComputerIsWhite      = COALESCE(ComputerIsWhite, 0),
                        CreatorColor         = COALESCE(CreatorColor, 'white'),
                        HandicapGold         = COALESCE(HandicapGold, 1);");

            migrationBuilder.AlterColumn<int>(
                name: "TimeIncrementSeconds",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "StartTimeMinutes",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "HandicapGold",
                table: "HistoricGames",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "HistoricGames",
                keyColumn: "CreatorColor",
                keyValue: null,
                column: "CreatorColor",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "CreatorColor",
                table: "HistoricGames",
                type: "varchar(6)",
                maxLength: 6,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(6)",
                oldMaxLength: 6,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<bool>(
                name: "ComputerIsWhite",
                table: "HistoricGames",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "TimeIncrementSeconds",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "StartTimeMinutes",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "HandicapGold",
                table: "GameSessions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "GameSessions",
                keyColumn: "CreatorColor",
                keyValue: null,
                column: "CreatorColor",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "CreatorColor",
                table: "GameSessions",
                type: "varchar(6)",
                maxLength: 6,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(6)",
                oldMaxLength: 6,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<bool>(
                name: "ComputerIsWhite",
                table: "GameSessions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)",
                oldNullable: true);
        }
    }
}
