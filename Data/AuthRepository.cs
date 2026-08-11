using Dapper;
using PpEvidencija.Models;

namespace PpEvidencija.Data;

public sealed class AuthRepository
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AuthRepository(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<AutentifikovaniKorisnik?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                ISNULL(CAST(korime AS nvarchar(100)), '') AS KorisnickoIme,
                ISNULL(CAST(ime AS nvarchar(100)), '') AS Ime,
                ISNULL(CAST(prezime AS nvarchar(100)), '') AS Prezime,
                ISNULL(CAST(lozinka AS nvarchar(200)), '') AS Lozinka,
                ISNULL(CAST(kriploz AS nvarchar(200)), '') AS LegacyLozinka,
                CAST(
                    CASE
                        WHEN aktivan IS NULL THEN 0
                        WHEN UPPER(LTRIM(RTRIM(CAST(aktivan AS nvarchar(20)))))
                             IN ('1', 'T', 'Y', 'TRUE', 'D', 'DA') THEN 1
                        ELSE 0
                    END AS bit
                ) AS Aktivan
            FROM dbo.a_user
            WHERE UPPER(LTRIM(RTRIM(CAST(korime AS nvarchar(100)))))
                = UPPER(LTRIM(RTRIM(@username)));
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AuthRow>(new CommandDefinition(
            sql,
            new { username },
            cancellationToken: cancellationToken));

        if (row is null || !row.Aktivan || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var unesenaLozinka = password;
        var odgovara = string.Equals(row.Lozinka?.Trim(), unesenaLozinka, StringComparison.Ordinal)
            || string.Equals(row.LegacyLozinka?.Trim(), unesenaLozinka, StringComparison.Ordinal);

        return odgovara
            ? new AutentifikovaniKorisnik(
                row.KorisnickoIme?.Trim() ?? username.Trim(),
                row.KorisnickoIme?.Trim() ?? username.Trim(),
                row.Ime?.Trim() ?? string.Empty,
                row.Prezime?.Trim() ?? string.Empty)
            : null;
    }

    private sealed class AuthRow
    {
        public string? KorisnickoIme { get; init; }
        public string? Ime { get; init; }
        public string? Prezime { get; init; }
        public string? Lozinka { get; init; }
        public string? LegacyLozinka { get; init; }
        public bool Aktivan { get; init; }
    }
}
