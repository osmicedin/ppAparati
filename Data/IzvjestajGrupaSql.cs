using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

internal static class IzvjestajGrupaSql
{
    internal const string Rows = """
        SELECT
            g.id AS GrupaId,
            g.verzija AS Verzija,
            LTRIM(RTRIM(g.konto)) AS Konto,
            ISNULL(kupac.Naziv, N'') AS NazivKupca,
            CAST(aparati.BrojAparata AS int) AS BrojAparata,
            aparati.DatumOd,
            aparati.DatumDo,
            g.zakljucen AS Zakljucen,
            ISNULL(g.posljednja_radnja, '') AS PosljednjaRadnjaKod,
            ISNULL(g.promijenio_korisnik, N'') AS PromijenioKorisnik,
            g.promijenjeno_utc AS PromijenjenoUtc,
            g.zakljuceno_utc AS ZakljucenoUtc
        FROM dbo.ppaparati_grupe AS g
        CROSS APPLY
        (
            SELECT
                COUNT_BIG(1) AS BrojAparata,
                MIN(p.datum_servisa) AS DatumOd,
                MAX(p.datum_servisa) AS DatumDo
            FROM dbo.ppaparati AS p
            WHERE p.grupa_id = g.id
        ) AS aparati
        OUTER APPLY
        (
            SELECT TOP (1)
                LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255)))) AS Naziv
            FROM dbo.konta AS k
            WHERE LTRIM(RTRIM(CAST(k.konto AS nvarchar(20)))) = g.konto
            ORDER BY LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255))))
        ) AS kupac
        WHERE aparati.BrojAparata > 0
        """;

    internal static string ValidateAccount(string konto)
    {
        if (string.IsNullOrWhiteSpace(konto) || konto.Trim().Length > 20)
        {
            throw new ArgumentException("Konto je obavezan i može imati najviše 20 znakova.", nameof(konto));
        }

        return konto.Trim();
    }

    internal static string ValidateUser(string korisnickoIme)
    {
        if (string.IsNullOrWhiteSpace(korisnickoIme) || korisnickoIme.Trim().Length > 100)
        {
            throw new ArgumentException(
                "Korisničko ime je obavezno i može imati najviše 100 znakova.", nameof(korisnickoIme));
        }

        return korisnickoIme.Trim();
    }

    internal static void ValidateVersion(long grupaId, long ocekivanaVerzija)
    {
        if (grupaId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grupaId));
        }

        if (ocekivanaVerzija <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ocekivanaVerzija));
        }
    }

    internal static DbString AccountParameter(string konto) => new()
    {
        Value = konto,
        IsAnsi = true,
        Length = 20
    };

    internal static async Task<string> GetAccountAsync(
        SqlConnection connection, long grupaId, CancellationToken cancellationToken)
    {
        // Read the immutable account before taking a transaction lock. Holding a row lock
        // while waiting for the account lock would invert the writer's lock order.
        var konto = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT konto FROM dbo.ppaparati_grupe WHERE id = @grupaId;",
            new { grupaId }, cancellationToken: cancellationToken));
        return konto is null ? throw new IzvjestajPromijenjenException() : konto.Trim();
    }

    internal static async Task LockAccountAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string konto,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        // CHECKSUM respects SQL collation equality, unlike a .NET-normalized lock name.
        // Hash collisions only serialize otherwise unrelated accounts. Readers and all
        // writers acquire this lock before touching group/record rows, including the
        // first insert, so reports are coherent without requiring snapshot isolation.
        const string sql = """
            DECLARE @resource nvarchar(255) =
                N'ppEvidencija:konto:' + CONVERT(nvarchar(12),
                    CHECKSUM(CAST(@konto AS varchar(20)) COLLATE DATABASE_DEFAULT));
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = @lockMode,
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """;
        var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { konto = AccountParameter(konto), lockMode = exclusive ? "Exclusive" : "Shared" },
            transaction,
            cancellationToken: cancellationToken));

        if (result < 0)
        {
            throw new InvalidOperationException(
                "Konto trenutno obrađuje drugi korisnik. Pokušajte ponovo nakon osvježavanja pregleda.");
        }
    }
}
