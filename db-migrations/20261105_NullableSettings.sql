START TRANSACTION;
DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `HistoricGames` MODIFY COLUMN `TimeIncrementSeconds` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `HistoricGames` MODIFY COLUMN `StartTimeMinutes` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `HistoricGames` MODIFY COLUMN `HandicapGold` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `HistoricGames` MODIFY COLUMN `CreatorColor` varchar(6) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `HistoricGames` MODIFY COLUMN `ComputerIsWhite` tinyint(1) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `GameSessions` MODIFY COLUMN `TimeIncrementSeconds` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `GameSessions` MODIFY COLUMN `StartTimeMinutes` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `GameSessions` MODIFY COLUMN `HandicapGold` int NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `GameSessions` MODIFY COLUMN `CreatorColor` varchar(6) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    ALTER TABLE `GameSessions` MODIFY COLUMN `ComputerIsWhite` tinyint(1) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN


                        UPDATE `GameSessions` SET
                            StartTimeMinutes     = IF(TimeControlEnabled, StartTimeMinutes, NULL),
                            TimeIncrementSeconds = IF(TimeControlEnabled, TimeIncrementSeconds, NULL),
                            ComputerIsWhite      = IF(ComputerLevel IS NULL, NULL, ComputerIsWhite),
                            CreatorColor         = IF(ComputerLevel IS NOT NULL OR BiddingEnabled OR CreatorColor = 'white', NULL, CreatorColor),
                            HandicapGold         = IF(Handicap = 'manual', HandicapGold, NULL);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN


                        UPDATE `HistoricGames` SET
                            StartTimeMinutes     = IF(TimeControlEnabled, StartTimeMinutes, NULL),
                            TimeIncrementSeconds = IF(TimeControlEnabled, TimeIncrementSeconds, NULL),
                            ComputerIsWhite      = IF(ComputerLevel IS NULL, NULL, ComputerIsWhite),
                            CreatorColor         = IF(ComputerLevel IS NOT NULL OR BiddingEnabled OR CreatorColor = 'white', NULL, CreatorColor),
                            HandicapGold         = IF(Handicap = 'manual', HandicapGold, NULL);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261105154835_NullableSettings') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261105154835_NullableSettings', '9.0.4');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

