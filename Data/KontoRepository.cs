using Dapper;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class KontoRepository
{
    public const string MissingAccountName = "Naziv konta nije pronađen";
    private readonly ErpConnectionFactory _connectionFactory;

    public KontoRepository(ErpConnectionFactory connectionFactory)
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
                WHERE konto LIKE '211%'
                  AND LEN(LTRIM(RTRIM(CAST(konto AS nvarchar(20))))) > 0
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
        var rows = await ErpQuery.ExecuteAsync(() => connection.QueryAsync<Konto>(new CommandDefinition(
            sql,
            new
            {
                maxResults = Math.Clamp(maxResults, 1, 200),
                kontoPattern = $"{escapedSearchText}%",
                nazivPattern = $"%{escapedSearchText}%"
            },
            cancellationToken: cancellationToken)), cancellationToken);
        return rows.AsList();
    }

    public async Task<bool> ExistsAsync(string konto, CancellationToken cancellationToken = default)
    {
        konto = IzvjestajGrupaSql.ValidateAccount(konto);
        if (!konto.StartsWith("211", StringComparison.Ordinal)) return false;

        const string sql = """
            SELECT CAST(CASE WHEN EXISTS
            (
                SELECT 1 FROM dbo.konta
                WHERE konto LIKE '211%'
                  AND LTRIM(RTRIM(CAST(konto AS nvarchar(20)))) = @konto
            ) THEN 1 ELSE 0 END AS bit);
            """;
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await ErpQuery.ExecuteAsync(() => connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, new { konto }, cancellationToken: cancellationToken)), cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetNamesAsync(
        IEnumerable<string> konta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(konta);
        var accounts = konta.Where(konto => !string.IsNullOrWhiteSpace(konto))
            .Select(konto => konto.Trim())
            .Where(konto => konto.Length <= 20 && konto.StartsWith("211", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (accounts.Length == 0) return names;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        // Returning each requested code preserves SQL collation matching without
        // attempting to reproduce that collation in an in-memory dictionary.
        foreach (var batch in accounts.Chunk(500))
        {
            var parameters = new DynamicParameters();
            var values = batch.Select((konto, index) =>
            {
                parameters.Add($"konto{index}", konto, System.Data.DbType.String, size: 20);
                return $"(@konto{index})";
            });
            var sql = $$"""
                SELECT requested.Sifra, kupac.Naziv
                FROM (VALUES {{string.Join(",", values)}}) AS requested(Sifra)
                CROSS APPLY
                (
                    SELECT TOP (1) LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255)))) AS Naziv
                    FROM dbo.konta AS k
                    WHERE k.konto LIKE '211%'
                      AND LTRIM(RTRIM(CAST(k.konto AS nvarchar(20)))) = requested.Sifra
                    ORDER BY LTRIM(RTRIM(CAST(k.naziv AS nvarchar(255))))
                ) AS kupac;
                """;
            var rows = await ErpQuery.ExecuteAsync(() => connection.QueryAsync<Konto>(new CommandDefinition(
                sql, parameters, cancellationToken: cancellationToken)), cancellationToken);
            foreach (var row in rows) names[row.Sifra] = row.Naziv;
        }
        return names;
    }

    internal async Task EnrichNamesAsync(
        IReadOnlyList<IzvjestajPregledRed> rows, CancellationToken cancellationToken)
    {
        var names = await GetNamesAsync(rows.Select(row => row.Konto), cancellationToken);
        foreach (var row in rows)
            row.NazivKupca = names.TryGetValue(row.Konto, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name : MissingAccountName;
    }
}
