using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class PpAparatRepository : IPpAparatRepository
{
    private readonly EvidencijaConnectionFactory _connectionFactory;
    private readonly KontoRepository _accounts;

    public PpAparatRepository(EvidencijaConnectionFactory connectionFactory, KontoRepository accounts)
    {
        _connectionFactory = connectionFactory;
        _accounts = accounts;
    }

    public async Task<string?> GetLastInspectorAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1) LTRIM(RTRIM(ispitivanje_izvrsio))
            FROM dbo.ppaparati
            WHERE LEN(LTRIM(RTRIM(ispitivanje_izvrsio))) > 0
            ORDER BY id DESC;
            """;
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
    }

    public async Task<long> InsertAsync(
        PpAparatInput input,
        string korisnickoIme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!ServiceDateRange.Contains(input.DatumServisa))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), "Datum servisa mora biti između 01.01.1900. i 30.06.9999.");
        }

        var konto = IzvjestajGrupaSql.ValidateAccount(input.Konto);
        korisnickoIme = IzvjestajGrupaSql.ValidateUser(korisnickoIme);
        if (!konto.StartsWith("211", StringComparison.Ordinal))
            throw new ArgumentException("Dozvoljena su samo konta koja počinju sa 211.", nameof(input));

        // Resolve ERP data before starting a local transaction or taking account locks.
        if (!await _accounts.ExistsAsync(konto, cancellationToken))
            throw new InvalidOperationException("Odabrani konto više ne postoji u ERP tabeli konta.");

        const string insertSql = """
            DECLARE @grupaId bigint;
            SELECT @grupaId = id
            FROM dbo.ppaparati_grupe
            WHERE konto = @Konto AND zakljucen = 0;

            IF @grupaId IS NULL
            BEGIN
                DECLARE @openedUtc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
                INSERT INTO dbo.ppaparati_grupe
                (
                    konto, zakljucen, verzija,
                    posljednja_radnja, promijenio_korisnik, promijenjeno_utc
                )
                VALUES (@Konto, 0, 1, 'O', @korisnickoIme, @openedUtc);
                SET @grupaId = CONVERT(bigint, SCOPE_IDENTITY());

                INSERT INTO dbo.ppaparati_grupe_audit
                    (grupa_id, radnja, korisnicko_ime, dogadjaj_utc)
                VALUES (@grupaId, 'O', @korisnickoIme, @openedUtc);
            END;

            UPDATE dbo.ppaparati_grupe
            SET verzija = verzija + 1
            WHERE id = @grupaId;

            INSERT INTO dbo.ppaparati
            (
                grupa_id,
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
                @grupaId,
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
            IsolationLevel.ReadCommitted, cancellationToken);
        await IzvjestajGrupaSql.LockAccountAsync(
            connection, transaction, konto, exclusive: true, cancellationToken);

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            insertSql,
            new
            {
                Konto = IzvjestajGrupaSql.AccountParameter(konto),
                korisnickoIme,
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

    public async Task<IzvjestajSnimak> GetForReportAsync(
        long grupaId,
        long ocekivanaVerzija,
        CancellationToken cancellationToken = default)
    {
        IzvjestajGrupaSql.ValidateVersion(grupaId, ocekivanaVerzija);

        const string recordsSql = """
            SELECT
                id AS Id,
                grupa_id AS GrupaId,
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
            WHERE grupa_id = @grupaId
            ORDER BY datum_servisa, id;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var konto = await IzvjestajGrupaSql.GetAccountAsync(connection, grupaId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await IzvjestajGrupaSql.LockAccountAsync(
            connection, transaction, konto, exclusive: false, cancellationToken);

        var grupa = await connection.QuerySingleOrDefaultAsync<IzvjestajPregledRed>(new CommandDefinition(
            IzvjestajGrupaSql.Rows + " AND g.id = @grupaId;",
            new { grupaId }, transaction, cancellationToken: cancellationToken));
        if (grupa is null || grupa.Verzija != ocekivanaVerzija)
        {
            throw new IzvjestajPromijenjenException();
        }

        var aparati = await connection.QueryAsync<PpAparatRecord>(new CommandDefinition(
            recordsSql, new { grupaId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        await _accounts.EnrichNamesAsync([grupa], cancellationToken);
        return new IzvjestajSnimak(grupa, aparati.AsList());
    }
}
