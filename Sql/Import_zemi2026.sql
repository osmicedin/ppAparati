/* Jednokratni prijenos PP evidencije iz ranije baze.
   Prvo pokrenuti Create_ppaparati.sql i zaustaviti aplikaciju.
   Izvorna baza i postojece nepovezane tabele ostaju nepromijenjene.
   a_user i konta se ne prenose: aplikacija ih cita iz konfigurisanog ERP-a.
   Skript odbija prijenos ako bilo koja ciljna PP tabela ima podatke. */
USE [evidencija];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() <> N'evidencija' OR DB_ID(N'zemi2026') IS NULL
    THROW 50010, 'Prijenos zahtijeva baze evidencija i zemi2026 na istoj instanci.', 1;

DECLARE @tables TABLE (sequence INT PRIMARY KEY, table_name SYSNAME NOT NULL);
INSERT @tables (sequence, table_name) VALUES
    (1, N'ppaparati_status'),
    (2, N'ppaparati_status_audit'),
    (3, N'ppaparati_grupe'),
    (4, N'ppaparati_grupe_audit'),
    (5, N'ppaparati');

DECLARE @sequence INT = 1, @table SYSNAME, @columns NVARCHAR(MAX), @compareColumns NVARCHAR(MAX),
        @sql NVARCHAR(MAX), @source NVARCHAR(300), @target NVARCHAR(300),
        @hasIdentity BIT, @identityTable NVARCHAR(300) = NULL, @count BIGINT;

BEGIN TRY
    BEGIN TRANSACTION;

    /* Zakljucaj svih pet izvornih tabela do kraja prijenosa, bez izmjena. */
    WHILE @sequence <= 5
    BEGIN
        SELECT @table = table_name FROM @tables WHERE sequence = @sequence;
        SET @target = N'dbo.' + QUOTENAME(@table);
        SET @source = N'[zemi2026].dbo.' + QUOTENAME(@table);
        IF OBJECT_ID(@target, N'U') IS NULL OR OBJECT_ID(@source, N'U') IS NULL
            THROW 50011, 'Nedostaje izvorna ili ciljna projektna tabela.', 1;

        SET @sql = N'SELECT @count = COUNT_BIG(*) FROM ' + @source
            + N' WITH (TABLOCK, HOLDLOCK);'
            + N' IF EXISTS (SELECT 1 FROM ' + @target + N' WITH (TABLOCKX, HOLDLOCK))'
            + N' THROW 50012, ''Ciljne projektne tabele moraju biti prazne; prijenos je otkazan.'', 1;';
        EXEC sys.sp_executesql @sql, N'@count bigint OUTPUT', @count OUTPUT;
        SET @sequence += 1;
    END;

    SET @sequence = 1;
    WHILE @sequence <= 5
    BEGIN
        SELECT @table = table_name FROM @tables WHERE sequence = @sequence;
        SET @target = N'dbo.' + QUOTENAME(@table);
        SET @source = N'[zemi2026].dbo.' + QUOTENAME(@table);

        /* Prenesi svaku fizicku kolonu ciljne sheme, ukljucujuci izvorne ID-eve.
           Izracunati sljedeci_servis SQL Server ponovo izracunava. */
        SELECT @columns = STRING_AGG(CONVERT(NVARCHAR(MAX), QUOTENAME(name)), N',')
                WITHIN GROUP (ORDER BY column_id),
               @compareColumns = STRING_AGG(CONVERT(NVARCHAR(MAX),
                    CASE WHEN collation_name IS NOT NULL
                        THEN N'CONVERT(nvarchar(max),' + QUOTENAME(name)
                            + N') COLLATE Latin1_General_100_BIN2'
                        ELSE QUOTENAME(name) END), N',')
                    WITHIN GROUP (ORDER BY column_id),
               @hasIdentity = CONVERT(BIT, MAX(CONVERT(INT, is_identity)))
        FROM sys.columns
        WHERE object_id = OBJECT_ID(@target) AND is_computed = 0;

        SET @sql = N'INSERT ' + @target + N' (' + @columns + N')'
            + N' SELECT ' + @columns + N' FROM ' + @source + N';';
        IF @hasIdentity = 1
        BEGIN
            SET @identityTable = @target;
            SET @sql = N'SET IDENTITY_INSERT ' + @target + N' ON;'
                + @sql + N' SET IDENTITY_INSERT ' + @target + N' OFF;';
        END;
        EXEC sys.sp_executesql @sql;
        SET @identityTable = NULL;

        /* Broj redova i sve vrijednosti moraju odgovarati izvoru prije COMMIT-a. */
        SET @sql = N'IF (SELECT COUNT_BIG(*) FROM ' + @source + N')'
            + N' <> (SELECT COUNT_BIG(*) FROM ' + @target + N')'
            + N' OR EXISTS (SELECT ' + @compareColumns + N' FROM ' + @source
            + N' EXCEPT SELECT ' + @compareColumns + N' FROM ' + @target + N')'
            + N' OR EXISTS (SELECT ' + @compareColumns + N' FROM ' + @target
            + N' EXCEPT SELECT ' + @compareColumns + N' FROM ' + @source + N')'
            + N' THROW 50013, ''Provjera prenesenih podataka nije uspjela.'', 1;';
        EXEC sys.sp_executesql @sql;
        SET @sequence += 1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    IF @identityTable IS NOT NULL
    BEGIN
        SET @sql = N'SET IDENTITY_INSERT ' + @identityTable + N' OFF;';
        EXEC sys.sp_executesql @sql;
    END;
    THROW;
END CATCH;

PRINT N'Projektni podaci su preneseni iz zemi2026 u evidencija i provjereni.';
