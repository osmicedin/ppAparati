using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class IzvjestajEvidencijaRepository : IIzvjestajEvidencijaRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public IzvjestajEvidencijaRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(
        IzvjestajStatusFilter statusFilter,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(statusFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(statusFilter));
        }

        var sql = $$"""
            WITH grupe AS
            (
                {{IzvjestajGrupaSql.Rows}}
            ), trenutne AS
            (
                SELECT *, ROW_NUMBER() OVER
                (
                    PARTITION BY Konto
                    ORDER BY Zakljucen, ZakljucenoUtc DESC, GrupaId DESC
                ) AS Redoslijed
                FROM grupe
            )
            SELECT *
            FROM trenutne
            WHERE Redoslijed = 1
              AND
              (
                  @statusFilter = 2
                  OR (@statusFilter = 1 AND Zakljucen = 1)
                  OR (@statusFilter = 0 AND Zakljucen = 0)
              )
            ORDER BY Konto, NazivKupca;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<IzvjestajPregledRed>(new CommandDefinition(
            sql, new { statusFilter = (int)statusFilter }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<IzvjestajPregledRed>> GetHistorijaAsync(
        string konto,
        CancellationToken cancellationToken = default)
    {
        konto = IzvjestajGrupaSql.ValidateAccount(konto);
        const string suffix = """
             AND g.konto = @konto
            ORDER BY g.zakljucen, g.zakljuceno_utc DESC, g.id DESC;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await IzvjestajGrupaSql.LockAccountAsync(
            connection, transaction, konto, exclusive: false, cancellationToken);
        var rows = await connection.QueryAsync<IzvjestajPregledRed>(new CommandDefinition(
            IzvjestajGrupaSql.Rows + suffix,
            new { konto = IzvjestajGrupaSql.AccountParameter(konto) },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return rows.AsList();
    }

    public async Task ZakljuciAsync(
        long grupaId,
        long ocekivanaVerzija,
        string korisnickoIme,
        CancellationToken cancellationToken = default)
    {
        IzvjestajGrupaSql.ValidateVersion(grupaId, ocekivanaVerzija);
        korisnickoIme = IzvjestajGrupaSql.ValidateUser(korisnickoIme);

        const string closeSql = """
            DECLARE @closedUtc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
            UPDATE dbo.ppaparati_grupe
            SET zakljucen = 1,
                verzija = verzija + 1,
                posljednja_radnja = 'Z',
                promijenio_korisnik = @korisnickoIme,
                promijenjeno_utc = @closedUtc,
                zakljuceno_utc = @closedUtc
            WHERE id = @grupaId
              AND verzija = @ocekivanaVerzija
              AND zakljucen = 0
              AND EXISTS (SELECT 1 FROM dbo.ppaparati WHERE grupa_id = @grupaId);
            DECLARE @changed int = @@ROWCOUNT;

            IF @changed = 1
            BEGIN
                INSERT INTO dbo.ppaparati_grupe_audit
                    (grupa_id, radnja, korisnicko_ime, dogadjaj_utc)
                VALUES (@grupaId, 'Z', @korisnickoIme, @closedUtc);
            END;
            SELECT @changed;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var konto = await IzvjestajGrupaSql.GetAccountAsync(connection, grupaId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await IzvjestajGrupaSql.LockAccountAsync(
            connection, transaction, konto, exclusive: true, cancellationToken);

        var changed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            closeSql,
            new { grupaId, ocekivanaVerzija, korisnickoIme },
            transaction,
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new IzvjestajPromijenjenException();
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
