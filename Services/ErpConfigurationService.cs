using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Data;
using PpEvidencija.Models;

namespace PpEvidencija.Services;

public sealed class ErpConfigurationService(EvidencijaConnectionFactory connectionFactory) : IErpConfigurationService
{
    public async Task<ErpConfigurationMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ErpConfigurationMetadata>(
            Command("dbo.pp_erp_config_metadata", null, cancellationToken));
    }

    public async Task TestAsync(ErpConfigurationInput input, CancellationToken cancellationToken = default)
    {
        var candidate = await ResolveCandidateAsync(input, cancellationToken);
        await TestCandidateAsync(candidate, cancellationToken);
    }

    public async Task SaveAsync(ErpConfigurationInput input, byte[]? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (expectedVersion is not null && expectedVersion.Length != 8)
            throw new ErpConfigurationException("Verzija postavki nije ispravna. Ponovo otvorite konfiguraciju.");
        var candidate = await ResolveCandidateAsync(input, cancellationToken);
        // Bind the connection that was actually tested to the optimistic concurrency token.
        if (!(expectedVersion is null && candidate.Version is null)
            && !(expectedVersion is not null && candidate.Version is not null && expectedVersion.SequenceEqual(candidate.Version)))
            throw new ErpConfigurationException("Postavke je u međuvremenu izmijenio drugi korisnik. Ponovo otvorite konfiguraciju.");
        await TestCandidateAsync(candidate, cancellationToken);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(Command("dbo.pp_erp_config_save", new
        {
            candidate.ServerName,
            candidate.DatabaseName,
            candidate.UserName,
            candidate.Password,
            ExpectedVersion = expectedVersion
        }, cancellationToken));
    }

    public async Task<ErpConnectionSnapshot> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ErpCredentials>(Command("dbo.pp_erp_config_read", null, cancellationToken));
        if (row is null)
            throw new ErpConfigurationException("ERP baza nije konfigurirana. Otvorite Konfiguracija ERP baze.");
        if (string.IsNullOrEmpty(row.Password))
            throw new ErpConfigurationException("Sačuvana ERP lozinka nije dostupna. Obratite se SQL administratoru.");
        return new ErpConnectionSnapshot(BuildConnectionString(row), row.Version ?? []);
    }

    private async Task<ErpCredentials> ResolveCandidateAsync(ErpConfigurationInput input, CancellationToken cancellationToken)
    {
        ValidateInput(input);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        // Test and save always use the password supplied in the form, never a saved credential.
        var previous = await connection.QuerySingleOrDefaultAsync<ErpConfigurationMetadata>(
            Command("dbo.pp_erp_config_metadata", null, cancellationToken));
        return new ErpCredentials
        {
            ServerName = input.ServerName.Trim(), DatabaseName = input.DatabaseName.Trim(),
            UserName = input.UserName.Trim(), Password = input.Password!, Version = previous?.Version
        };
    }

    private static async Task TestCandidateAsync(ErpCredentials candidate, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await new ErpConnectionFactory(BuildConnectionString(candidate)).OpenAsync(cancellationToken);
            // TOP(1) checks actual SELECT permission and schema compatibility without copying users.
            await connection.ExecuteAsync(new CommandDefinition("""
                SELECT TOP (1) CAST(korime AS nvarchar(100)), CAST(ime AS nvarchar(100)),
                    CAST(prezime AS nvarchar(100)), CAST(lozinka AS nvarchar(200)),
                    CAST(kriploz AS nvarchar(200)), CAST(aktivan AS nvarchar(20)) FROM dbo.a_user;
                SELECT TOP (1) CAST(konto AS nvarchar(20)), CAST(naziv AS nvarchar(255))
                    FROM dbo.konta WHERE konto LIKE '211%';
                """, commandTimeout: 15, cancellationToken: cancellationToken));
        }
        catch (SqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ErpConfigurationException("ERP konekcija nema pristup potrebnim kolonama tabela a_user i konta, ili upit nije uspio.");
        }
    }

    private static string BuildConnectionString(ErpCredentials credentials) => new SqlConnectionStringBuilder
    {
        DataSource = credentials.ServerName, InitialCatalog = credentials.DatabaseName,
        UserID = credentials.UserName, Password = credentials.Password,
        IntegratedSecurity = false, Encrypt = true, TrustServerCertificate = true,
        PersistSecurityInfo = false, ConnectTimeout = 8, ApplicationName = "ppEvidencija ERP"
    }.ConnectionString;

    private static void ValidateInput(ErpConfigurationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (new[] { input.ServerName, input.DatabaseName, input.UserName }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 || value.Contains('\0')))
            throw new ErpConfigurationException("Instanca, naziv baze i SQL korisnik su obavezni i mogu imati najviše 128 znakova.");
        if (string.IsNullOrWhiteSpace(input.Password))
            throw new ErpConfigurationException("Unesite SQL lozinku za testiranje i spremanje ERP konekcije.");
        if (input.Password is { Length: > 128 } || input.Password?.Contains('\0') == true)
            throw new ErpConfigurationException("SQL lozinka može imati najviše 128 znakova i ne smije sadržavati nedozvoljene znakove.");
    }

    private static CommandDefinition Command(string procedure, object? parameters, CancellationToken cancellationToken) =>
        new(procedure, parameters, commandType: CommandType.StoredProcedure, commandTimeout: 15, cancellationToken: cancellationToken);

    private sealed class ErpCredentials
    {
        public string ServerName { get; init; } = string.Empty;
        public string DatabaseName { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public byte[]? Version { get; init; }
    }
}
