START TRANSACTION;
DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261101123256_HistoricGamePlayerIndexes') THEN

    CREATE INDEX `IX_HistoricGames_BlackPlayerId` ON `HistoricGames` (`BlackPlayerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261101123256_HistoricGamePlayerIndexes') THEN

    CREATE INDEX `IX_HistoricGames_WhitePlayerId` ON `HistoricGames` (`WhitePlayerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261101123256_HistoricGamePlayerIndexes') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261101123256_HistoricGamePlayerIndexes', '9.0.4');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

