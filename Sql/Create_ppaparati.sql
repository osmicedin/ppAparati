SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 50001, 'Skript pokrenite nad glavnom poslovnom bazom, ne nad sistemskom bazom.', 1;
END;

/* Pokrenuti uz zaustavljene stare verzije aplikacije na svim instalacijama. */
BEGIN TRY
BEGIN TRANSACTION;

/* Preimenuj tabele iz prethodne verzije bez gubitka podataka. */
IF OBJECT_ID(N'dbo.ppaparati_status', N'U') IS NULL
   AND OBJECT_ID(N'dbo.ppizvjestaji_status', N'U') IS NOT NULL
BEGIN
    EXEC sys.sp_rename N'dbo.ppizvjestaji_status', N'ppaparati_status';
END;

IF OBJECT_ID(N'dbo.ppaparati_status_audit', N'U') IS NULL
   AND OBJECT_ID(N'dbo.ppizvjestaji_status_audit', N'U') IS NOT NULL
BEGIN
    EXEC sys.sp_rename N'dbo.ppizvjestaji_status_audit', N'ppaparati_status_audit';
END;

IF OBJECT_ID(N'dbo.ppaparati', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppaparati
    (
        id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_ppaparati PRIMARY KEY,
        konto VARCHAR(20) NOT NULL,
        tip NVARCHAR(50) NOT NULL,
        punjenje_kg DECIMAL(8, 2) NOT NULL,
        serijski_broj_aparata NVARCHAR(100) NOT NULL,
        godina_proizvodnje SMALLINT NOT NULL,
        datum_servisa DATE NOT NULL,
        sljedeci_servis AS CONVERT(date, DATEADD(MONTH, 6, datum_servisa)) PERSISTED,
        konstatacija_ispravnosti NVARCHAR(100) NOT NULL,
        vozilo NVARCHAR(100) NOT NULL,
        ispitivanje_izvrsio NVARCHAR(150) NOT NULL,

        CONSTRAINT CK_ppaparati_konto_not_blank
            CHECK (LEN(LTRIM(RTRIM(konto))) > 0),
        CONSTRAINT CK_ppaparati_tip_not_blank
            CHECK (LEN(LTRIM(RTRIM(tip))) > 0),
        CONSTRAINT CK_ppaparati_punjenje_positive
            CHECK (punjenje_kg > 0),
        CONSTRAINT CK_ppaparati_serijski_broj_not_blank
            CHECK (LEN(LTRIM(RTRIM(serijski_broj_aparata))) > 0),
        CONSTRAINT CK_ppaparati_godina
            CHECK (godina_proizvodnje BETWEEN 1900 AND 9999),
        CONSTRAINT CK_ppaparati_konstatacija_not_blank
            CHECK (LEN(LTRIM(RTRIM(konstatacija_ispravnosti))) > 0),
        CONSTRAINT CK_ppaparati_vozilo_not_blank
            CHECK (LEN(LTRIM(RTRIM(vozilo))) > 0),
        CONSTRAINT CK_ppaparati_ispitivac_not_blank
            CHECK (LEN(LTRIM(RTRIM(ispitivanje_izvrsio))) > 0)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ppaparati')
      AND name = N'IX_ppaparati_konto_datum_servisa'
)
BEGIN
    CREATE INDEX IX_ppaparati_konto_datum_servisa
        ON dbo.ppaparati (konto, datum_servisa)
        INCLUDE
        (
            tip,
            punjenje_kg,
            serijski_broj_aparata,
            godina_proizvodnje,
            konstatacija_ispravnosti,
            vozilo,
            ispitivanje_izvrsio
        );
END;

IF OBJECT_ID(N'dbo.ppaparati_status', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppaparati_status
    (
        id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_ppaparati_status PRIMARY KEY,
        konto VARCHAR(20) NOT NULL,
        godina SMALLINT NOT NULL,
        mjesec TINYINT NOT NULL,
        zakljucen BIT NOT NULL,
        posljednja_radnja CHAR(1) NOT NULL,
        promijenio_korisnik NVARCHAR(100) NOT NULL,
        promijenjeno_utc DATETIMEOFFSET(0) NOT NULL,

        CONSTRAINT UQ_ppaparati_status_period
            UNIQUE (konto, godina, mjesec),
        CONSTRAINT CK_ppaparati_status_konto_not_blank
            CHECK (LEN(LTRIM(RTRIM(konto))) > 0),
        CONSTRAINT CK_ppaparati_status_godina
            CHECK (godina BETWEEN 1900 AND 9999),
        CONSTRAINT CK_ppaparati_status_mjesec
            CHECK (mjesec BETWEEN 1 AND 12),
        CONSTRAINT CK_ppaparati_status_radnja
            CHECK (posljednja_radnja IN ('Z', 'O')),
        CONSTRAINT CK_ppaparati_status_stanje_radnja
            CHECK (
                (zakljucen = 1 AND posljednja_radnja = 'Z')
                OR (zakljucen = 0 AND posljednja_radnja = 'O')
            ),
        CONSTRAINT CK_ppaparati_status_korisnik_not_blank
            CHECK (LEN(LTRIM(RTRIM(promijenio_korisnik))) > 0)
    );
END;

IF OBJECT_ID(N'dbo.ppaparati_status_audit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppaparati_status_audit
    (
        id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_ppaparati_status_audit PRIMARY KEY,
        status_id BIGINT NOT NULL,
        radnja CHAR(1) NOT NULL,
        korisnicko_ime NVARCHAR(100) NOT NULL,
        dogadjaj_utc DATETIMEOFFSET(0) NOT NULL,

        CONSTRAINT FK_ppaparati_status_audit_status
            FOREIGN KEY (status_id)
            REFERENCES dbo.ppaparati_status (id),
        CONSTRAINT CK_ppaparati_status_audit_radnja
            CHECK (radnja IN ('Z', 'O')),
        CONSTRAINT CK_ppaparati_status_audit_korisnik_not_blank
            CHECK (LEN(LTRIM(RTRIM(korisnicko_ime))) > 0)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ppaparati_status_audit')
      AND name IN
      (
          N'IX_ppaparati_status_audit_status_dogadjaj',
          N'IX_ppizvjestaji_status_audit_status_dogadjaj'
      )
)
BEGIN
    CREATE INDEX IX_ppaparati_status_audit_status_dogadjaj
        ON dbo.ppaparati_status_audit (status_id, dogadjaj_utc DESC);
END;

/* GROUP_MIGRATION_BEGIN: legacy statuses and their audit remain unchanged. */
IF OBJECT_ID(N'dbo.ppaparati_grupe', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppaparati_grupe
    (
        id BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ppaparati_grupe PRIMARY KEY,
        konto VARCHAR(20) NOT NULL,
        zakljucen BIT NOT NULL,
        verzija BIGINT NOT NULL,
        posljednja_radnja CHAR(1) NULL,
        promijenio_korisnik NVARCHAR(100) NULL,
        promijenjeno_utc DATETIMEOFFSET(7) NULL,
        zakljuceno_utc DATETIMEOFFSET(7) NULL,
        izvorni_status_id BIGINT NULL,
        CONSTRAINT UQ_ppaparati_grupe_id_konto UNIQUE (id, konto),
        CONSTRAINT FK_ppaparati_grupe_izvorni_status FOREIGN KEY (izvorni_status_id)
            REFERENCES dbo.ppaparati_status (id),
        CONSTRAINT CK_ppaparati_grupe_konto_not_blank CHECK (LEN(LTRIM(RTRIM(konto))) > 0),
        CONSTRAINT CK_ppaparati_grupe_verzija CHECK (verzija >= 1),
        CONSTRAINT CK_ppaparati_grupe_radnja CHECK (posljednja_radnja IN ('O', 'Z')),
        CONSTRAINT CK_ppaparati_grupe_korisnik_not_blank
            CHECK (promijenio_korisnik IS NULL OR LEN(LTRIM(RTRIM(promijenio_korisnik))) > 0),
        CONSTRAINT CK_ppaparati_grupe_stanje CHECK
        (
            (zakljucen = 0 AND zakljuceno_utc IS NULL
                AND (posljednja_radnja IS NULL OR posljednja_radnja = 'O'))
            OR (zakljucen = 1 AND zakljuceno_utc IS NOT NULL AND posljednja_radnja = 'Z')
        ),
        CONSTRAINT CK_ppaparati_grupe_promjena CHECK
        (
            (posljednja_radnja IS NULL AND promijenio_korisnik IS NULL AND promijenjeno_utc IS NULL)
            OR (posljednja_radnja IS NOT NULL AND promijenio_korisnik IS NOT NULL AND promijenjeno_utc IS NOT NULL)
        )
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ppaparati_grupe')
    AND name = N'IX_ppaparati_grupe_konto_id')
    CREATE INDEX IX_ppaparati_grupe_konto_id ON dbo.ppaparati_grupe (konto, id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ppaparati_grupe')
    AND name = N'UX_ppaparati_grupe_otvoreno_konto')
    CREATE UNIQUE INDEX UX_ppaparati_grupe_otvoreno_konto
        ON dbo.ppaparati_grupe (konto) WHERE zakljucen = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ppaparati_grupe')
    AND name = N'UX_ppaparati_grupe_izvorni_status')
    CREATE UNIQUE INDEX UX_ppaparati_grupe_izvorni_status
        ON dbo.ppaparati_grupe (izvorni_status_id) WHERE izvorni_status_id IS NOT NULL;

IF OBJECT_ID(N'dbo.ppaparati_grupe_audit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppaparati_grupe_audit
    (
        id BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ppaparati_grupe_audit PRIMARY KEY,
        grupa_id BIGINT NOT NULL,
        radnja CHAR(1) NOT NULL,
        korisnicko_ime NVARCHAR(100) NOT NULL,
        dogadjaj_utc DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT FK_ppaparati_grupe_audit_grupa FOREIGN KEY (grupa_id)
            REFERENCES dbo.ppaparati_grupe (id),
        CONSTRAINT CK_ppaparati_grupe_audit_radnja CHECK (radnja IN ('O', 'Z')),
        CONSTRAINT CK_ppaparati_grupe_audit_korisnik_not_blank
            CHECK (LEN(LTRIM(RTRIM(korisnicko_ime))) > 0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ppaparati_grupe_audit')
    AND name = N'IX_ppaparati_grupe_audit_grupa_dogadjaj')
    CREATE INDEX IX_ppaparati_grupe_audit_grupa_dogadjaj
        ON dbo.ppaparati_grupe_audit (grupa_id, dogadjaj_utc DESC, id DESC);

IF COL_LENGTH(N'dbo.ppaparati', N'grupa_id') IS NULL
    ALTER TABLE dbo.ppaparati ADD grupa_id BIGINT NULL;

/* Dynamic batch sees grupa_id even when it was added in this transaction. */
EXEC sys.sp_executesql N'
    DECLARE @broj_prije BIGINT;
    SELECT @broj_prije = COUNT_BIG(*) FROM dbo.ppaparati WITH (TABLOCKX, HOLDLOCK);

    /* Each previously closed monthly period remains an independent group. */
    INSERT dbo.ppaparati_grupe
        (konto, zakljucen, verzija, posljednja_radnja, promijenio_korisnik,
         promijenjeno_utc, zakljuceno_utc, izvorni_status_id)
    SELECT s.konto, 1, 1, s.posljednja_radnja, s.promijenio_korisnik,
           s.promijenjeno_utc, s.promijenjeno_utc, s.id
    FROM dbo.ppaparati_status s
    WHERE s.zakljucen = 1
      AND EXISTS
      (
          SELECT 1 FROM dbo.ppaparati p
          WHERE p.grupa_id IS NULL AND p.konto = s.konto
            AND YEAR(p.datum_servisa) = s.godina AND MONTH(p.datum_servisa) = s.mjesec
      )
      AND NOT EXISTS (SELECT 1 FROM dbo.ppaparati_grupe g WHERE g.izvorni_status_id = s.id);

    UPDATE p SET grupa_id = g.id
    FROM dbo.ppaparati p
    INNER JOIN dbo.ppaparati_status s ON s.konto = p.konto
        AND s.godina = YEAR(p.datum_servisa) AND s.mjesec = MONTH(p.datum_servisa)
        AND s.zakljucen = 1
    INNER JOIN dbo.ppaparati_grupe g ON g.izvorni_status_id = s.id AND g.konto = p.konto
    WHERE p.grupa_id IS NULL;

    /* Open and missing monthly statuses merge. No fictional migration user/event. */
    INSERT dbo.ppaparati_grupe (konto, zakljucen, verzija)
    SELECT DISTINCT p.konto, 0, 1 FROM dbo.ppaparati p
    WHERE p.grupa_id IS NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.ppaparati_grupe g WHERE g.konto = p.konto AND g.zakljucen = 0);

    UPDATE p SET grupa_id = g.id
    FROM dbo.ppaparati p
    INNER JOIN dbo.ppaparati_grupe g ON g.konto = p.konto AND g.zakljucen = 0
    WHERE p.grupa_id IS NULL;

    IF EXISTS
    (
        SELECT 1 FROM dbo.ppaparati p LEFT JOIN dbo.ppaparati_grupe g
            ON g.id = p.grupa_id AND g.konto = p.konto
        WHERE g.id IS NULL
    ) OR @broj_prije <> (SELECT COUNT_BIG(*) FROM dbo.ppaparati)
        THROW 50002, ''Migracija grupa nije dodijelila svaki aparat odgovarajucem kontu.'', 1;

    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N''dbo.ppaparati'')
        AND name = N''grupa_id'' AND is_nullable = 1)
        ALTER TABLE dbo.ppaparati ALTER COLUMN grupa_id BIGINT NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N''dbo.ppaparati'')
        AND name = N''FK_ppaparati_grupa_konto'')
        ALTER TABLE dbo.ppaparati WITH CHECK ADD CONSTRAINT FK_ppaparati_grupa_konto
            FOREIGN KEY (grupa_id, konto) REFERENCES dbo.ppaparati_grupe (id, konto);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''dbo.ppaparati'')
        AND name = N''IX_ppaparati_grupa_datum_servisa'')
        CREATE INDEX IX_ppaparati_grupa_datum_servisa ON dbo.ppaparati (grupa_id, datum_servisa, id);
';

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

PRINT N'Tabele za PP aparate su spremne u bazi ' + QUOTENAME(DB_NAME()) + N'.';
