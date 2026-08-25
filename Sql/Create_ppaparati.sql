SET NOCOUNT ON;
SET XACT_ABORT ON;

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
GO

IF OBJECT_ID(N'dbo.ppizvjestaji_status', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppizvjestaji_status
    (
        id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_ppizvjestaji_status PRIMARY KEY,
        konto VARCHAR(20) NOT NULL,
        godina SMALLINT NOT NULL,
        mjesec TINYINT NOT NULL,
        zakljucen BIT NOT NULL,
        posljednja_radnja CHAR(1) NOT NULL,
        promijenio_korisnik NVARCHAR(100) NOT NULL,
        promijenjeno_utc DATETIMEOFFSET(0) NOT NULL,

        CONSTRAINT UQ_ppizvjestaji_status_period
            UNIQUE (konto, godina, mjesec),
        CONSTRAINT CK_ppizvjestaji_status_konto_not_blank
            CHECK (LEN(LTRIM(RTRIM(konto))) > 0),
        CONSTRAINT CK_ppizvjestaji_status_godina
            CHECK (godina BETWEEN 1900 AND 9999),
        CONSTRAINT CK_ppizvjestaji_status_mjesec
            CHECK (mjesec BETWEEN 1 AND 12),
        CONSTRAINT CK_ppizvjestaji_status_radnja
            CHECK (posljednja_radnja IN ('Z', 'O')),
        CONSTRAINT CK_ppizvjestaji_status_stanje_radnja
            CHECK (
                (zakljucen = 1 AND posljednja_radnja = 'Z')
                OR (zakljucen = 0 AND posljednja_radnja = 'O')
            ),
        CONSTRAINT CK_ppizvjestaji_status_korisnik_not_blank
            CHECK (LEN(LTRIM(RTRIM(promijenio_korisnik))) > 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.ppizvjestaji_status_audit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ppizvjestaji_status_audit
    (
        id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_ppizvjestaji_status_audit PRIMARY KEY,
        status_id BIGINT NOT NULL,
        radnja CHAR(1) NOT NULL,
        korisnicko_ime NVARCHAR(100) NOT NULL,
        dogadjaj_utc DATETIMEOFFSET(0) NOT NULL,

        CONSTRAINT FK_ppizvjestaji_status_audit_status
            FOREIGN KEY (status_id)
            REFERENCES dbo.ppizvjestaji_status (id),
        CONSTRAINT CK_ppizvjestaji_status_audit_radnja
            CHECK (radnja IN ('Z', 'O')),
        CONSTRAINT CK_ppizvjestaji_status_audit_korisnik_not_blank
            CHECK (LEN(LTRIM(RTRIM(korisnicko_ime))) > 0)
    );

    CREATE INDEX IX_ppizvjestaji_status_audit_status_dogadjaj
        ON dbo.ppizvjestaji_status_audit (status_id, dogadjaj_utc DESC);
END;
GO
