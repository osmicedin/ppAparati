using Dapper;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class KontoRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public KontoRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<Konto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) AS Sifra,
                LTRIM(RTRIM(CAST(naziv AS nvarchar(255)))) AS Naziv
            FROM dbo.konta
            WHERE LEN(LTRIM(RTRIM(CAST(konto AS nvarchar(20))))) > 0
            ORDER BY naziv, konto;
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Konto>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
