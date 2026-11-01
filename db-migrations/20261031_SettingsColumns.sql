START TRANSACTION;
DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `BiddingEnabled` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `RequestedMapSeed` longtext CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `MapMode` varchar(1) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'm';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `IsRanked` tinyint(1) NOT NULL DEFAULT TRUE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `ComputerLevel` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `ComputerIsWhite` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `CreatorColor` varchar(6) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'white';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `Handicap` varchar(6) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'none';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `HandicapGold` int NOT NULL DEFAULT 1;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `WhiteHandicapGold` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` ADD `BlackHandicapGold` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    UPDATE GameSessions SET
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

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `GameSessions` DROP COLUMN `SettingsJson`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `BiddingEnabled` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `RequestedMapSeed` longtext CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `MapMode` varchar(1) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'm';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `IsRanked` tinyint(1) NOT NULL DEFAULT TRUE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `ComputerLevel` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `ComputerIsWhite` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `CreatorColor` varchar(6) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'white';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `Handicap` varchar(6) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'none';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `HandicapGold` int NOT NULL DEFAULT 1;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `WhiteHandicapGold` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` ADD `BlackHandicapGold` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    UPDATE HistoricGames SET
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

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    ALTER TABLE `HistoricGames` DROP COLUMN `SettingsJson`;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261031162646_SettingsColumns') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261031162646_SettingsColumns', '9.0.4');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

