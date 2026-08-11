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

    public async Task<IReadOnlyList<Konto>> SearchByPrefixAsync(
        string kontoPrefix,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT TOP (@maxResults)
                LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) AS Sifra,
                LTRIM(RTRIM(CAST(naziv AS nvarchar(255)))) AS Naziv
            FROM dbo.konta
            WHERE LEN(LTRIM(RTRIM(CAST(konto AS nvarchar(20))))) > 0
              AND LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) LIKE @kontoPattern ESCAPE N'~'
            ORDER BY Sifra, Naziv;
            """;

        var escapedPrefix = kontoPrefix
            .Trim()
            .Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            .Replace("[", "~[", StringComparison.Ordinal);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<Konto>(new CommandDefinition(
            sql,
            new
            {
                maxResults = Math.Clamp(maxResults, 1, 200),
                kontoPattern = $"{escapedPrefix}%"
            },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
