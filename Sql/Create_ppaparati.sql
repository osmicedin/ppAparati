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
