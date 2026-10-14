/* Configuration schema and automatic encryption setup. Existing keys are preserved.
   tools/Initialize-ErpConfiguration.ps1 optionally exports recovery backups and provisions
   a restricted runtime login. No default password or ERP source is seeded. */
USE [evidencija];
GO

/* ERP_SCHEMA_BEGIN: schema-only batches used by isolated integration tests. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50104, 'ERP konfiguracija zahtijeva korisnicku bazu.', 1;

IF OBJECT_ID(N'dbo.pp_erp_config', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pp_erp_config
    (
        Id TINYINT NOT NULL CONSTRAINT PK_pp_erp_config PRIMARY KEY,
        ServerName NVARCHAR(128) NOT NULL,
        DatabaseName NVARCHAR(128) NOT NULL,
        UserName NVARCHAR(128) NOT NULL,
        PasswordCipher VARBINARY(8000) NOT NULL,
        ChangedUtc DATETIMEOFFSET(7) NOT NULL,
        Version ROWVERSION NOT NULL,
        CONSTRAINT CK_pp_erp_config_singleton CHECK (Id = 1),
        CONSTRAINT CK_pp_erp_config_names CHECK
            (LEN(LTRIM(RTRIM(ServerName))) > 0 AND LEN(LTRIM(RTRIM(DatabaseName))) > 0
             AND LEN(LTRIM(RTRIM(UserName))) > 0)
    );
END;

IF DATABASE_PRINCIPAL_ID(N'ppEvidencija_runtime') IS NULL
    CREATE ROLE ppEvidencija_runtime AUTHORIZATION dbo;

/* Upgrade: remove the retired configuration-password API, retaining any old table/data. */
DROP PROCEDURE IF EXISTS dbo.pp_erp_config_read_for_admin;
DROP PROCEDURE IF EXISTS dbo.pp_erp_admin_verify;
DROP PROCEDURE IF EXISTS dbo.pp_erp_admin_challenge;
GO

CREATE OR ALTER PROCEDURE dbo.pp_erp_ensure_encryption
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @StartedTransaction BIT = 0, @LockResult INT, @HasConfiguration BIT = 0,
            @HasMasterKey BIT, @HasCertificate BIT, @HasKey BIT,
            @MasterPassword NVARCHAR(128), @CreateMasterKey NVARCHAR(400),
            @Probe VARBINARY(8000);
    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;
        /* Schema setup and saves acquire the same locks in the same order. */
        EXEC @LockResult = sys.sp_getapplock @Resource = N'ppEvidencija.ERP.Encryption',
            @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
        IF @LockResult < 0
            THROW 50103, 'Priprema ERP sifrovanja trenutno nije dostupna.', 1;
        IF EXISTS (SELECT 1 FROM dbo.pp_erp_config WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1)
            SET @HasConfiguration = 1;
        SET @HasMasterKey = CASE WHEN EXISTS
            (SELECT 1 FROM sys.symmetric_keys WHERE name = N'##MS_DatabaseMasterKey##') THEN 1 ELSE 0 END;
        SET @HasCertificate = CASE WHEN EXISTS
            (SELECT 1 FROM sys.certificates WHERE name = N'pp_ErpConfigurationCertificate') THEN 1 ELSE 0 END;
        SET @HasKey = CASE WHEN EXISTS
            (SELECT 1 FROM sys.symmetric_keys WHERE name = N'pp_ErpConfigurationKey') THEN 1 ELSE 0 END;
        /* Never replace a lost part of an existing encryption chain or orphan saved data. */
        IF (@HasMasterKey = 0 AND (@HasCertificate = 1 OR @HasKey = 1 OR @HasConfiguration = 1))
           OR (@HasCertificate = 0 AND (@HasKey = 1 OR @HasConfiguration = 1))
           OR (@HasKey = 0 AND @HasConfiguration = 1)
            THROW 50103, 'Postojeci ERP kljuc nedostaje. Potreban je oporavak iz sigurnosne kopije.', 1;
        IF @HasMasterKey = 0
        BEGIN
            /* SQL also protects this DMK with its service master key. The random password
               stays in this session; recovery requires SQL key backups, never app settings. */
            SET @MasterPassword = N'Pp!9' + CONVERT(NVARCHAR(96), CRYPT_GEN_RANDOM(48), 2);
            IF @MasterPassword IS NULL
                THROW 50103, 'Generisanje kljuca za ERP sifrovanje nije uspjelo.', 1;
            SET @CreateMasterKey = N'CREATE MASTER KEY ENCRYPTION BY PASSWORD = N''' + @MasterPassword + N''';';
            EXEC sys.sp_executesql @CreateMasterKey;
            SET @MasterPassword = NULL;
            SET @CreateMasterKey = NULL;
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.databases
                       WHERE database_id = DB_ID() AND is_master_key_encrypted_by_server = 1)
            THROW 50103, 'Glavni kljuc baze zahtijeva oporavak na ovoj SQL instanci.', 1;
        IF @HasCertificate = 0
            CREATE CERTIFICATE pp_ErpConfigurationCertificate
                WITH SUBJECT = N'ppEvidencija central ERP configuration';
        IF @HasKey = 0
            CREATE SYMMETRIC KEY pp_ErpConfigurationKey WITH ALGORITHM = AES_256
                ENCRYPTION BY CERTIFICATE pp_ErpConfigurationCertificate;
        IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys
                       WHERE name = N'pp_ErpConfigurationKey' AND algorithm_desc = N'AES_256')
            THROW 50103, 'ERP kljuc ima nepodrzan algoritam.', 1;
        OPEN SYMMETRIC KEY pp_ErpConfigurationKey DECRYPTION BY CERTIFICATE pp_ErpConfigurationCertificate;
        SET @Probe = ENCRYPTBYKEY(KEY_GUID(N'pp_ErpConfigurationKey'), 0x01020304, 1, N'ppEvidencija.ERP.Probe');
        IF @Probe IS NULL OR DECRYPTBYKEY(@Probe, 1, N'ppEvidencija.ERP.Probe') <> 0x01020304
           OR DECRYPTBYKEY(@Probe, 1, N'ppEvidencija.ERP.Probe') IS NULL
            THROW 50103, 'Provjera ERP sifrovanja nije uspjela.', 1;
        CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
        IF @StartedTransaction = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        SET @MasterPassword = NULL;
        SET @CreateMasterKey = NULL;
        IF EXISTS (SELECT 1 FROM sys.openkeys WHERE key_name = N'pp_ErpConfigurationKey')
            CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
        IF @StartedTransaction = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW 50103, 'ERP kljuc nije dostupan ili priprema sifrovanja nije uspjela.', 1;
    END CATCH;
END;
GO

/* Basic configuration is ready to save immediately; no interactive key setup is required. */
EXEC dbo.pp_erp_ensure_encryption;
GO

CREATE OR ALTER PROCEDURE dbo.pp_erp_config_metadata
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ServerName, DatabaseName, UserName, CONVERT(BINARY(8), Version) AS Version, ChangedUtc
    FROM dbo.pp_erp_config WHERE Id = 1;
END;
GO

CREATE OR ALTER PROCEDURE dbo.pp_erp_config_read
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    /* Capture one coherent revision before decryption; do not mix concurrent saves. */
    DECLARE @ServerName NVARCHAR(128), @DatabaseName NVARCHAR(128), @UserName NVARCHAR(128),
            @Cipher VARBINARY(8000), @Version BINARY(8), @ChangedUtc DATETIMEOFFSET(7),
            @Password NVARCHAR(128);
    SELECT @ServerName = ServerName, @DatabaseName = DatabaseName, @UserName = UserName,
           @Cipher = PasswordCipher, @Version = Version, @ChangedUtc = ChangedUtc
    FROM dbo.pp_erp_config WHERE Id = 1;
    IF @Version IS NULL
    BEGIN
        SELECT ServerName, DatabaseName, UserName, CONVERT(BINARY(8), Version) AS Version,
               ChangedUtc, CONVERT(NVARCHAR(128), NULL) AS Password
        FROM dbo.pp_erp_config WHERE 1 = 0;
        RETURN;
    END;
    BEGIN TRY
        OPEN SYMMETRIC KEY pp_ErpConfigurationKey DECRYPTION BY CERTIFICATE pp_ErpConfigurationCertificate;
        SET @Password = CONVERT(NVARCHAR(128),
            DECRYPTBYKEY(@Cipher, 1, N'ppEvidencija.ERP.1'));
        CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
        IF @Password IS NULL
            THROW 50103, 'ERP lozinka se ne moze desifrovati. Obratite se administratoru baze.', 1;
    END TRY
    BEGIN CATCH
        IF EXISTS (SELECT 1 FROM sys.openkeys WHERE key_name = N'pp_ErpConfigurationKey')
            CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
        THROW 50103, 'ERP lozinka se ne moze desifrovati. Obratite se administratoru baze.', 1;
    END CATCH;
    SELECT @ServerName AS ServerName, @DatabaseName AS DatabaseName, @UserName AS UserName,
           @Version AS Version, @ChangedUtc AS ChangedUtc, @Password AS Password;
END;
GO

CREATE OR ALTER PROCEDURE dbo.pp_erp_config_save
    @ServerName NVARCHAR(4000),
    @DatabaseName NVARCHAR(4000),
    @UserName NVARCHAR(4000),
    @Password NVARCHAR(4000) = NULL,
    @ExpectedVersion VARBINARY(8000) = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @ServerName IS NULL OR @DatabaseName IS NULL OR @UserName IS NULL
       OR LEN(LTRIM(RTRIM(@ServerName))) = 0 OR DATALENGTH(@ServerName) > 256
       OR LEN(LTRIM(RTRIM(@DatabaseName))) = 0 OR DATALENGTH(@DatabaseName) > 256
       OR LEN(LTRIM(RTRIM(@UserName))) = 0 OR DATALENGTH(@UserName) > 256
       OR @Password IS NULL OR DATALENGTH(@Password) = 0 OR DATALENGTH(@Password) > 256
       OR (@ExpectedVersion IS NOT NULL AND DATALENGTH(@ExpectedVersion) <> 8)
        THROW 50104, 'Neispravni ili predugi parametri ERP konfiguracije.', 1;
    /* Match .NET IsNullOrWhiteSpace without trimming a valid password before encryption. */
    DECLARE @PasswordIndex INT = 1, @PasswordHasContent BIT = 0;
    WHILE @PasswordIndex <= DATALENGTH(@Password) / 2 AND @PasswordHasContent = 0
    BEGIN
        IF UNICODE(SUBSTRING(@Password, @PasswordIndex, 1)) NOT IN
            (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196,
             8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)
            SET @PasswordHasContent = 1;
        SET @PasswordIndex += 1;
    END;
    IF @PasswordHasContent = 0
        THROW 50104, 'Unesite SQL lozinku za ERP konekciju.', 1;
    DECLARE @CurrentVersion BINARY(8), @Cipher VARBINARY(8000), @ChangedUtc DATETIMEOFFSET(7);
    BEGIN TRY
        BEGIN TRANSACTION;
        EXEC dbo.pp_erp_ensure_encryption;
        SELECT @CurrentVersion = Version
        FROM dbo.pp_erp_config WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1;
        IF (@CurrentVersion IS NULL AND @ExpectedVersion IS NOT NULL)
           OR (@CurrentVersion IS NOT NULL AND (@ExpectedVersion IS NULL OR @CurrentVersion <> @ExpectedVersion))
            THROW 50102, 'Konfiguraciju je promijenio drugi korisnik. Ponovo je ucitajte.', 1;
        BEGIN TRY
            OPEN SYMMETRIC KEY pp_ErpConfigurationKey DECRYPTION BY CERTIFICATE pp_ErpConfigurationCertificate;
            SET @Cipher = ENCRYPTBYKEY(KEY_GUID(N'pp_ErpConfigurationKey'),
                CONVERT(VARBINARY(256), @Password), 1, N'ppEvidencija.ERP.1');
            CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
            IF @Cipher IS NULL THROW 50103, 'ERP lozinka se ne moze sifrovati.', 1;
        END TRY
        BEGIN CATCH
            IF EXISTS (SELECT 1 FROM sys.openkeys WHERE key_name = N'pp_ErpConfigurationKey')
                CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
            THROW 50103, 'ERP kljuc nije dostupan ili sifrovanje nije uspjelo.', 1;
        END CATCH;
        SET @ChangedUtc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
        IF @CurrentVersion IS NULL
            INSERT dbo.pp_erp_config (Id, ServerName, DatabaseName, UserName, PasswordCipher, ChangedUtc)
            VALUES (1, LTRIM(RTRIM(@ServerName)), LTRIM(RTRIM(@DatabaseName)), LTRIM(RTRIM(@UserName)), @Cipher, @ChangedUtc);
        ELSE
            UPDATE dbo.pp_erp_config SET ServerName = LTRIM(RTRIM(@ServerName)),
                DatabaseName = LTRIM(RTRIM(@DatabaseName)), UserName = LTRIM(RTRIM(@UserName)),
                PasswordCipher = @Cipher, ChangedUtc = @ChangedUtc WHERE Id = 1;
        SELECT @CurrentVersion = Version FROM dbo.pp_erp_config WHERE Id = 1;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        IF EXISTS (SELECT 1 FROM sys.openkeys WHERE key_name = N'pp_ErpConfigurationKey')
            CLOSE SYMMETRIC KEY pp_ErpConfigurationKey;
        THROW;
    END CATCH;
    SELECT LTRIM(RTRIM(@ServerName)) AS ServerName, LTRIM(RTRIM(@DatabaseName)) AS DatabaseName,
           LTRIM(RTRIM(@UserName)) AS UserName, @CurrentVersion AS Version, @ChangedUtc AS ChangedUtc;
END;
GO

/* All runtime users can configure ERP through the procedures. Direct writes remain unavailable. */
IF OBJECT_ID(N'dbo.pp_config_admin', N'U') IS NOT NULL
    DENY SELECT, INSERT, UPDATE, DELETE ON dbo.pp_config_admin TO ppEvidencija_runtime;
DENY SELECT, INSERT, UPDATE, DELETE ON dbo.pp_erp_config TO ppEvidencija_runtime;
GRANT EXECUTE ON dbo.pp_erp_config_metadata TO ppEvidencija_runtime;
GRANT EXECUTE ON dbo.pp_erp_config_read TO ppEvidencija_runtime;
GRANT EXECUTE ON dbo.pp_erp_config_save TO ppEvidencija_runtime;
GRANT SELECT, INSERT ON dbo.ppaparati TO ppEvidencija_runtime;
GRANT SELECT, INSERT, UPDATE ON dbo.ppaparati_grupe TO ppEvidencija_runtime;
GRANT SELECT, INSERT ON dbo.ppaparati_grupe_audit TO ppEvidencija_runtime;
GRANT SELECT ON dbo.ppaparati_status TO ppEvidencija_runtime;
GRANT SELECT ON dbo.ppaparati_status_audit TO ppEvidencija_runtime;
