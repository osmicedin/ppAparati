using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class IzvjestajEvidencijaRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public IzvjestajEvidencijaRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(
        int month,
        int year,
        IzvjestajStatusFilter statusFilter,
        CancellationToken cancellationToken = default)
    {
        ValidatePeriod(month, year);

        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);

        const string sql = """
            WITH period AS
            (
                SELECT
                    LTRIM(RTRIM(p.konto)) AS Konto,
                    COUNT_BIG(1) AS BrojAparata
                FROM dbo.ppaparati AS p
                WHERE p.datum_servisa >= @from
                  AND p.datum_servisa < @to
                GROUP BY LTRIM(RTRIM(p.konto))
            )
            SELECT
                period.Konto,
                ISNULL(kupac.Naziv, N'') AS NazivKupca,
                CAST(period.BrojAparata AS int) AS BrojAparata,
                CAST(ISNULL(status.zakljucen, 0) AS bit) AS Zakljucen,
                ISNULL(status.posljednja_radnja, '') AS PosljednjaRadnjaKod,
                ISNULL(status.promijenio_korisnik, N'') AS PromijenioKorisnik,
                status.promijenjeno_utc AS PromijenjenoUtc
            FROM period
            OUTER APPLY
            (
                SELECT TOP (1)
                    LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255)))) AS Naziv
                FROM dbo.konta AS k
                WHERE LTRIM(RTRIM(CAST(k.konto AS nvarchar(20)))) = period.Konto
                ORDER BY LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255))))
            ) AS kupac
            LEFT JOIN dbo.ppizvjestaji_status AS status
                ON status.konto = period.Konto
               AND status.godina = @year
               AND status.mjesec = @month
            WHERE @statusFilter = 2
               OR (@statusFilter = 1 AND ISNULL(status.zakljucen, 0) = 1)
               OR (@statusFilter = 0 AND ISNULL(status.zakljucen, 0) = 0)
            ORDER BY period.Konto, kupac.Naziv;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<IzvjestajPregledRed>(new CommandDefinition(
            sql,
            new { from, to, month, year, statusFilter = (int)statusFilter },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task PromijeniStatusAsync(
        string konto,
        int month,
        int year,
        bool ocekivanoZakljucen,
        bool zakljucen,
        string korisnickoIme,
        CancellationToken cancellationToken = default)
    {
        konto = konto.Trim();
        korisnickoIme = korisnickoIme.Trim();
        ValidatePeriod(month, year);

        if (string.IsNullOrWhiteSpace(konto))
        {
            throw new ArgumentException("Konto je obavezan.", nameof(konto));
        }

        if (string.IsNullOrWhiteSpace(korisnickoIme))
        {
            throw new ArgumentException("Korisničko ime je obavezno.", nameof(korisnickoIme));
        }

        if (ocekivanoZakljucen == zakljucen)
        {
            throw new ArgumentException("Novi status mora biti različit od očekivanog statusa.", nameof(zakljucen));
        }

        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);

        const string selectStatusSql = """
            SELECT TOP (1)
                id AS Id,
                zakljucen AS Zakljucen
            FROM dbo.ppizvjestaji_status WITH (UPDLOCK, HOLDLOCK)
            WHERE konto = @konto
              AND godina = @year
              AND mjesec = @month;
            """;

        const string countRecordsSql = """
            SELECT COUNT_BIG(1)
            FROM dbo.ppaparati WITH (HOLDLOCK)
            WHERE konto = @konto
              AND datum_servisa >= @from
              AND datum_servisa < @to;
            """;

        const string insertStatusSql = """
            INSERT INTO dbo.ppizvjestaji_status
            (
                konto,
                godina,
                mjesec,
                zakljucen,
                posljednja_radnja,
                promijenio_korisnik,
                promijenjeno_utc
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @konto,
                @year,
                @month,
                @zakljucen,
                @radnja,
                @korisnickoIme,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );
            """;

        const string updateStatusSql = """
            UPDATE dbo.ppizvjestaji_status
            SET zakljucen = @zakljucen,
                posljednja_radnja = @radnja,
                promijenio_korisnik = @korisnickoIme,
                promijenjeno_utc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE id = @id;
            """;

        const string insertAuditSql = """
            INSERT INTO dbo.ppizvjestaji_status_audit
            (
                status_id,
                radnja,
                korisnicko_ime,
                dogadjaj_utc
            )
            VALUES
            (
                @statusId,
                @radnja,
                @korisnickoIme,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            );
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var commandParameters = new { konto, month, year };
        var current = await connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
            selectStatusSql,
            commandParameters,
            transaction,
            cancellationToken: cancellationToken));

        var trenutnoZakljucen = current?.Zakljucen ?? false;
        if (trenutnoZakljucen != ocekivanoZakljucen)
        {
            throw new InvalidOperationException(
                "Status perioda je u međuvremenu promijenjen. Osvježite pregled i pokušajte ponovo.");
        }

        var recordCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            countRecordsSql,
            new { konto, from, to },
            transaction,
            cancellationToken: cancellationToken));

        if (recordCount == 0)
        {
            throw new InvalidOperationException("Za odabrani konto i period više nema podataka za izvještaj.");
        }

        var radnja = zakljucen ? "Z" : "O";
        long statusId;

        if (current is null)
        {
            statusId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                insertStatusSql,
                new { konto, month, year, zakljucen, radnja, korisnickoIme },
                transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            statusId = current.Id;
            await connection.ExecuteAsync(new CommandDefinition(
                updateStatusSql,
                new { id = statusId, zakljucen, radnja, korisnickoIme },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            insertAuditSql,
            new { statusId, radnja, korisnickoIme },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidatePeriod(int month, int year)
    {
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Mjesec mora biti između 1 i 12.");
        }

        if (year is < 1900 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Godina mora biti između 1900 i 9999.");
        }
    }

    private sealed class StatusRow
    {
        public long Id { get; init; }
        public bool Zakljucen { get; init; }
    }
}
