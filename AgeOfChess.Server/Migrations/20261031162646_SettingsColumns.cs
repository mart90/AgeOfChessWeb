using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgeOfChess.Server.Migrations
{
    /// <summary>
    /// Game settings move from the SettingsJson column to columns of their own (GameSettings as an
    /// owned type). BoardSize and the time control already had columns. Values are copied out of the
    /// JSON before it's dropped; keys missing from older rows get GameSettings' defaults.
    /// </summary>
    public partial class SettingsColumns : Migration
    {
        private static readonly string[] Tables = ["GameSessions", "HistoricGames"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.AddColumn<bool>(name: "BiddingEnabled", table: table, type: "tinyint(1)", nullable: false, defaultValue: false);
                migrationBuilder.AddColumn<string>(name: "RequestedMapSeed", table: table, type: "longtext", nullable: true)
                    .Annotation("MySql:CharSet", "utf8mb4");
                migrationBuilder.AddColumn<string>(name: "MapMode", table: table, type: "varchar(1)", maxLength: 1, nullable: false, defaultValue: "m")
                    .Annotation("MySql:CharSet", "utf8mb4");
                migrationBuilder.AddColumn<bool>(name: "IsRanked", table: table, type: "tinyint(1)", nullable: false, defaultValue: true);
                migrationBuilder.AddColumn<int>(name: "ComputerLevel", table: table, type: "int", nullable: true);
                migrationBuilder.AddColumn<bool>(name: "ComputerIsWhite", table: table, type: "tinyint(1)", nullable: false, defaultValue: false);
                migrationBuilder.AddColumn<string>(name: "CreatorColor", table: table, type: "varchar(6)", maxLength: 6, nullable: false, defaultValue: "white")
                    .Annotation("MySql:CharSet", "utf8mb4");
                migrationBuilder.AddColumn<string>(name: "Handicap", table: table, type: "varchar(6)", maxLength: 6, nullable: false, defaultValue: "none")
                    .Annotation("MySql:CharSet", "utf8mb4");
                migrationBuilder.AddColumn<int>(name: "HandicapGold", table: table, type: "int", nullable: false, defaultValue: 1);
                migrationBuilder.AddColumn<int>(name: "WhiteHandicapGold", table: table, type: "int", nullable: false, defaultValue: 0);
                migrationBuilder.AddColumn<int>(name: "BlackHandicapGold", table: table, type: "int", nullable: false, defaultValue: 0);

                migrationBuilder.Sql($"""
                    UPDATE {table} SET
                        BiddingEnabled    = COALESCE(JSON_VALUE(SettingsJson, '$.BiddingEnabled' RETURNING UNSIGNED), 0),
                        RequestedMapSeed  = JSON_VALUE(SettingsJson, '$.MapSeed'),
                        MapMode           = COALESCE(JSON_VALUE(SettingsJson, '$.MapMode'), 'm'),
                        IsRanked          = COALESCE(JSON_VALUE(SettingsJson, '$.IsRanked' RETURNING UNSIGNED), 1),
                        ComputerLevel     = JSON_VALUE(SettingsJson, '$.ComputerLevel' RETURNING SIGNED),
                        ComputerIsWhite   = COALESCE(JSON_VALUE(SettingsJson, '$.ComputerIsWhite' RETURNING UNSIGNED), 0),
                        CreatorColor      = COALESCE(JSON_VALUE(SettingsJson, '$.CreatorColor'), 'white'),
                        Handicap          = COALESCE(JSON_VALUE(SettingsJson, '$.Handicap'), 'none'),
                        HandicapGold      = COALESCE(JSON_VALUE(SettingsJson, '$.HandicapGold' RETURNING SIGNED), 1),
                        WhiteHandicapGold = COALESCE(JSON_VALUE(SettingsJson, '$.WhiteHandicapGold' RETURNING SIGNED), 0),
                        BlackHandicapGold = COALESCE(JSON_VALUE(SettingsJson, '$.BlackHandicapGold' RETURNING SIGNED), 0)
                    WHERE JSON_VALID(SettingsJson);
                    """);

                migrationBuilder.DropColumn(name: "SettingsJson", table: table);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.AddColumn<string>(name: "SettingsJson", table: table, type: "longtext", nullable: false)
                    .Annotation("MySql:CharSet", "utf8mb4");

                // JSON booleans (MySQL's own are just 0 / 1, which System.Text.Json won't read as bool)
                static string B(string column) => $"IF({column}, CAST('true' AS JSON), CAST('false' AS JSON))";
                migrationBuilder.Sql($"""
                    UPDATE {table} SET SettingsJson = JSON_OBJECT(
                        'BoardSize', BoardSize, 'TimeControlEnabled', {B("TimeControlEnabled")},
                        'StartTimeMinutes', StartTimeMinutes, 'TimeIncrementSeconds', TimeIncrementSeconds,
                        'BiddingEnabled', {B("BiddingEnabled")}, 'MapSeed', RequestedMapSeed, 'MapMode', MapMode,
                        'IsRanked', {B("IsRanked")}, 'ComputerLevel', ComputerLevel, 'ComputerIsWhite', {B("ComputerIsWhite")},
                        'CreatorColor', CreatorColor, 'Handicap', Handicap, 'HandicapGold', HandicapGold,
                        'WhiteHandicapGold', WhiteHandicapGold, 'BlackHandicapGold', BlackHandicapGold);
                    """);

                foreach (var column in new[] { "BiddingEnabled", "RequestedMapSeed", "MapMode", "IsRanked", "ComputerLevel",
                                               "ComputerIsWhite", "CreatorColor", "Handicap", "HandicapGold",
                                               "WhiteHandicapGold", "BlackHandicapGold" })
                    migrationBuilder.DropColumn(name: column, table: table);
            }
        }
    }
}
