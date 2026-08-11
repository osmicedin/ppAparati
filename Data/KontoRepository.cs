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

    public async Task<IReadOnlyList<Konto>> SearchAsync(
        string searchText,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        const string sql = """
            SELECT TOP (@maxResults)
                filtrirano.Sifra,
                filtrirano.Naziv
            FROM
            (
                SELECT DISTINCT
                    LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) AS Sifra,
                    LTRIM(RTRIM(CAST(naziv AS nvarchar(255)))) AS Naziv
                FROM dbo.konta
                WHERE LEN(LTRIM(RTRIM(CAST(konto AS nvarchar(20))))) > 0
            ) AS filtrirano
            WHERE filtrirano.Sifra LIKE @kontoPattern ESCAPE N'~'
               OR filtrirano.Naziv LIKE @nazivPattern ESCAPE N'~'
            ORDER BY filtrirano.Sifra, filtrirano.Naziv;
            """;

        var escapedSearchText = searchText
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
                kontoPattern = $"{escapedSearchText}%",
                nazivPattern = $"%{escapedSearchText}%"
            },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
