using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class PpAparatRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public PpAparatRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> InsertAsync(PpAparatInput input, CancellationToken cancellationToken = default)
    {
        const string accountExistsSql = """
            SELECT COUNT_BIG(1)
            FROM dbo.konta
            WHERE LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) = @konto;
            """;

        const string insertSql = """
            INSERT INTO dbo.ppaparati
            (
                konto,
                tip,
                punjenje_kg,
                serijski_broj_aparata,
                godina_proizvodnje,
                datum_servisa,
                konstatacija_ispravnosti,
                vozilo,
                ispitivanje_izvrsio
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @Konto,
                @Tip,
                @PunjenjeKg,
                @SerijskiBroj,
                @GodinaProizvodnje,
                @DatumServisa,
                @KonstatacijaIspravnosti,
                @Vozilo,
                @IspitivanjeIzvrsio
            );
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var accountCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            accountExistsSql,
            new { konto = input.Konto },
            transaction,
            cancellationToken: cancellationToken));

        if (accountCount == 0)
        {
            throw new InvalidOperationException("Odabrani konto više ne postoji u tabeli konta.");
        }

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            insertSql,
            new
            {
                input.Konto,
                input.Tip,
                input.PunjenjeKg,
                input.SerijskiBroj,
                input.GodinaProizvodnje,
                DatumServisa = input.DatumServisa.Date,
                input.KonstatacijaIspravnosti,
                input.Vozilo,
                input.IspitivanjeIzvrsio
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<PpAparatRecord>> GetForReportAsync(
        string konto,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);

        const string sql = """
            SELECT
                id AS Id,
                LTRIM(RTRIM(konto)) AS Konto,
                LTRIM(RTRIM(tip)) AS Tip,
                punjenje_kg AS PunjenjeKg,
                LTRIM(RTRIM(serijski_broj_aparata)) AS SerijskiBroj,
                godina_proizvodnje AS GodinaProizvodnje,
                datum_servisa AS DatumServisa,
                sljedeci_servis AS SljedeciServis,
                LTRIM(RTRIM(konstatacija_ispravnosti)) AS KonstatacijaIspravnosti,
                LTRIM(RTRIM(vozilo)) AS Vozilo,
                LTRIM(RTRIM(ispitivanje_izvrsio)) AS IspitivanjeIzvrsio
            FROM dbo.ppaparati
            WHERE konto = @konto
              AND datum_servisa >= @from
              AND datum_servisa < @to
            ORDER BY datum_servisa, id;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PpAparatRecord>(new CommandDefinition(
            sql,
            new { konto, from, to },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT YEAR(datum_servisa)
            FROM dbo.ppaparati
            ORDER BY YEAR(datum_servisa) DESC;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var years = await connection.QueryAsync<int>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return years.AsList();
    }
}
