using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;

namespace PpEvidencija.Tests;

public sealed class ConfigurationSecurityTests
{
    [Fact]
    public void SensitiveObjectsDoNotPrintCredentials()
    {
        const string password = "Test-only secret!";
        Assert.DoesNotContain(password, new ErpConfigurationInput("server", "db", "user", password).ToString()!);
        Assert.DoesNotContain(password, new ErpConnectionSnapshot($"Server=test;Password={password}", new byte[8]).ToString()!);
        Assert.Equal("Safe error", DatabaseErrorMessages.ForUser(new Exception(password), "Safe error"));
    }

    [Fact]
    public async Task InvalidConnectionFieldsAreRejectedBeforeOpeningAConnection()
    {
        var service = new ErpConfigurationService(new EvidencijaConnectionFactory("invalid connection string"));
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.TestAsync(new("", "db", "user", "password")));
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.SaveAsync(new("server", "db", "user", "password"), new byte[7]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0\u2003")]
    public async Task MissingPasswordIsRejectedBeforeOpeningAConnection(string? password)
    {
        var service = new ErpConfigurationService(new EvidencijaConnectionFactory("invalid connection string"));
        var input = new ErpConfigurationInput("server", "db", "user", password);
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.TestAsync(input));
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.SaveAsync(input, new byte[8]));
    }
}

public sealed class ErpConfigurationIntegrationTests : IClassFixture<ErpConfigurationDatabase>
{
    private readonly ErpConfigurationDatabase _db;
    public ErpConfigurationIntegrationTests(ErpConfigurationDatabase database) => _db = database;

    [SqlServerFact]
    public async Task ConfigurationEncryptsExplicitPasswordAndEnforcesConcurrency()
    {
        await _db.ResetAsync();
        var service = _db.Service;
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.OpenSessionAsync());
        Assert.Null(await service.GetMetadataAsync());
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.TestAsync(_db.Input(password: null)));
        await service.SaveAsync(_db.Input(), null);
        var metadata = (await service.GetMetadataAsync())!;
        var cipher = await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config");
        Assert.NotEqual(Encoding.Unicode.GetBytes(_db.SourcePassword), cipher);
        Assert.True(cipher.Length > 32);
        var snapshot = await service.OpenSessionAsync();
        var versionCopy = snapshot.Version;
        versionCopy[0] ^= 0xff;
        Assert.Equal(metadata.Version, snapshot.Version);
        await using (var source = await snapshot.ConnectionFactory.OpenAsync())
            Assert.Equal(_db.SourceDatabase, await source.ExecuteScalarAsync<string>("SELECT DB_NAME()"));

        await service.SaveAsync(_db.Input(), metadata.Version);
        var updated = (await service.GetMetadataAsync())!;
        Assert.False(metadata.Version.SequenceEqual(updated.Version));
        await Assert.ThrowsAsync<ErpConfigurationException>(() => service.SaveAsync(_db.Input(), metadata.Version));
        await using var runtime = new SqlConnection(_db.RuntimeConnectionString);
        var conflict = await Assert.ThrowsAsync<SqlException>(() => runtime.ExecuteAsync("""
            EXEC dbo.pp_erp_config_save @ServerName, @DatabaseName, @UserName,
                @Password = @password, @ExpectedVersion = @Version;
            """, new { ServerName = _db.Server, DatabaseName = _db.SourceDatabase,
                UserName = _db.SourceLogin, password = _db.SourcePassword, metadata.Version }));
        Assert.Equal(50102, conflict.Number);
        await _db.ApplyConfigurationSchemaAsync();
        Assert.Equal(updated.Version, (await service.GetMetadataAsync())!.Version);
        await service.OpenSessionAsync();
    }

    [SqlServerFact]
    public async Task MissingPasswordCannotTestOrOverwriteSavedConfigurationThroughServiceOrSql()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var metadata = (await _db.Service.GetMetadataAsync())!;
        var cipher = await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config");
        var identity = await _db.EncryptionIdentityAsync();
        await using var runtime = new SqlConnection(_db.RuntimeConnectionString);
        foreach (var password in new string?[] { null, "", "   ", "\t\r\n", "\u00a0\u2003" })
        {
            await Assert.ThrowsAsync<ErpConfigurationException>(() => _db.Service.TestAsync(_db.Input(password)));
            await Assert.ThrowsAsync<ErpConfigurationException>(() => _db.Service.SaveAsync(_db.Input(password), metadata.Version));
            var error = await Assert.ThrowsAsync<SqlException>(() => runtime.ExecuteAsync("""
                EXEC dbo.pp_erp_config_save @ServerName, @DatabaseName, @UserName,
                    @Password = @password, @ExpectedVersion = @Version;
                """, new { ServerName = _db.Server, DatabaseName = _db.SourceDatabase,
                    UserName = _db.SourceLogin, password, metadata.Version }));
            Assert.Equal(50104, error.Number);
            Assert.Equal(metadata.Version, (await _db.Service.GetMetadataAsync())!.Version);
            Assert.Equal(cipher, await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
            Assert.Equal(identity, await _db.EncryptionIdentityAsync());
        }
        await _db.Service.OpenSessionAsync();
    }

    [SqlServerFact]
    public async Task FirstRuntimeSaveCreatesMissingEncryptionKeysAfterConnectionTestSucceeds()
    {
        await _db.ResetAsync();
        await _db.RemoveEncryptionKeysAsync(includeMasterKey: true);
        try
        {
            Assert.Equal(0, await _db.EncryptionObjectCountAsync());
            await _db.Service.TestAsync(_db.Input());
            Assert.Equal(0, await _db.EncryptionObjectCountAsync());
            Assert.Null(await _db.Service.GetMetadataAsync());

            // Reproduces the production failure: connection test works, but Save
            // must also initialize the local encryption hierarchy as the restricted user.
            await _db.Service.SaveAsync(_db.Input(), null);

            Assert.Equal(3, await _db.EncryptionObjectCountAsync());
            Assert.Equal("AES_256", await _db.Owner.ScalarAsync<string>(
                "SELECT algorithm_desc FROM sys.symmetric_keys WHERE name=N'pp_ErpConfigurationKey';"));
            var metadata = (await _db.Service.GetMetadataAsync())!;
            var snapshot = await _db.Service.OpenSessionAsync();
            await using (var source = await snapshot.ConnectionFactory.OpenAsync())
                Assert.Equal(_db.SourceDatabase, await source.ExecuteScalarAsync<string>("SELECT DB_NAME()"));
            await _db.Service.SaveAsync(_db.Input(), metadata.Version);
            await _db.Service.OpenSessionAsync();
        }
        finally { await _db.ApplyConfigurationSchemaAsync(); }
    }

    [SqlServerFact]
    public async Task ExistingMasterKeyIsPreservedWhenCertificateAndApplicationKeyAreProvisioned()
    {
        await _db.ResetAsync();
        await _db.ApplyConfigurationSchemaAsync();
        var masterKey = await _db.Owner.ScalarAsync<Guid>(
            "SELECT key_guid FROM sys.symmetric_keys WHERE name=N'##MS_DatabaseMasterKey##';");
        await _db.RemoveEncryptionKeysAsync(includeMasterKey: false);
        try
        {
            Assert.Equal(1, await _db.EncryptionObjectCountAsync());
            await _db.Service.SaveAsync(_db.Input(), null);
            Assert.Equal(masterKey, await _db.Owner.ScalarAsync<Guid>(
                "SELECT key_guid FROM sys.symmetric_keys WHERE name=N'##MS_DatabaseMasterKey##';"));
            Assert.Equal(3, await _db.EncryptionObjectCountAsync());
            await _db.Service.OpenSessionAsync();
        }
        finally { await _db.ApplyConfigurationSchemaAsync(); }
    }

    [SqlServerFact]
    public async Task SchemaRerunAndPasswordEditsPreserveExistingEncryptionKeys()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var identity = await _db.EncryptionIdentityAsync();
        var cipher = await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config");
        var metadata = (await _db.Service.GetMetadataAsync())!;
        await _db.ApplyConfigurationSchemaAsync();
        Assert.Equal(identity, await _db.EncryptionIdentityAsync());
        Assert.Equal(cipher, await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
        await _db.Service.SaveAsync(_db.Input(), metadata.Version);
        Assert.Equal(identity, await _db.EncryptionIdentityAsync());
        Assert.NotEqual(Encoding.Unicode.GetBytes(_db.SourcePassword),
            await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
        metadata = (await _db.Service.GetMetadataAsync())!;
        await _db.Service.SaveAsync(_db.Input(), metadata.Version);
        Assert.Equal(identity, await _db.EncryptionIdentityAsync());
        await _db.Service.OpenSessionAsync();
    }

    [SqlServerFact]
    public async Task ConcurrentFirstSavesProvisionOneHierarchyAndRejectTheStaleSave()
    {
        await _db.ResetAsync();
        await _db.RemoveEncryptionKeysAsync(includeMasterKey: true);
        try
        {
            var outcomes = await Task.WhenAll(
                Record.ExceptionAsync(() => _db.Service.SaveAsync(_db.Input(), null)),
                Record.ExceptionAsync(() => _db.Service.SaveAsync(_db.Input(), null)));
            Assert.Single(outcomes, error => error is null);
            var conflict = Assert.Single(outcomes, error => error is not null);
            if (conflict is SqlException sql) Assert.Equal(50102, sql.Number);
            else Assert.Contains("izmijenio", Assert.IsType<ErpConfigurationException>(conflict).Message);
            Assert.Equal(3, await _db.EncryptionObjectCountAsync());
            Assert.Equal(1, await _db.Owner.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.pp_erp_config"));
            await _db.Service.OpenSessionAsync();
        }
        finally { await _db.ApplyConfigurationSchemaAsync(); }
    }

    [SqlServerFact]
    public async Task MissingKeyForSavedCipherFailsClosedWithoutReplacingTheEncryptionHierarchy()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var metadata = (await _db.Service.GetMetadataAsync())!;
        var cipher = await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config");
        // Simulate damaged installation only inside this generated disposable database.
        await _db.Owner.ExecuteAsync("DROP SYMMETRIC KEY pp_ErpConfigurationKey;");
        var remainingIdentity = await _db.EncryptionIdentityAsync();
        try
        {
            Assert.Equal(50103, (await Assert.ThrowsAsync<SqlException>(() => _db.Service.OpenSessionAsync())).Number);
            await Assert.ThrowsAsync<ErpConfigurationException>(() =>
                _db.Service.SaveAsync(_db.Input(password: null), metadata.Version));
            Assert.Equal(50103, (await Assert.ThrowsAsync<SqlException>(() =>
                _db.Service.SaveAsync(_db.Input(), metadata.Version))).Number);
            Assert.Equal(50103, (await Assert.ThrowsAsync<SqlException>(() => _db.ApplyConfigurationSchemaAsync())).Number);
            Assert.Equal(remainingIdentity, await _db.EncryptionIdentityAsync());
            Assert.Equal(2, await _db.EncryptionObjectCountAsync());
            Assert.Equal(cipher, await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
            Assert.Equal(metadata.Version, (await _db.Service.GetMetadataAsync())!.Version);
        }
        finally
        {
            // Delete the deliberately unrecoverable test row before restoring this fixture.
            await _db.ResetAsync();
            await _db.ApplyConfigurationSchemaAsync();
        }
    }

    [SqlServerFact]
    public async Task RuntimeCanConfigureWithoutAdministratorTableButCannotReadOrWriteProtectedRowsDirectly()
    {
        await _db.ResetAsync();
        Assert.Equal(0, await _db.Owner.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = N'pp_config_admin';"));
        Assert.Equal(0, await _db.Owner.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.procedures
            WHERE name IN (N'pp_erp_admin_challenge', N'pp_erp_admin_verify', N'pp_erp_config_read_for_admin');
            """));
        Assert.Null(await _db.Service.GetMetadataAsync());
        await _db.Service.TestAsync(_db.Input());
        Assert.Equal(0, await _db.Owner.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.pp_erp_config"));
        await _db.Service.SaveAsync(_db.Input(), null);
        var metadata = (await _db.Service.GetMetadataAsync())!;
        Assert.Equal(_db.SourceDatabase, metadata.DatabaseName);
        await _db.Service.TestAsync(_db.Input());
        await _db.Service.OpenSessionAsync();

        await using var runtime = new SqlConnection(_db.RuntimeConnectionString);
        var denied = await Assert.ThrowsAsync<SqlException>(() => runtime.QueryAsync("SELECT * FROM dbo.pp_erp_config"));
        Assert.Equal(229, denied.Number);
        denied = await Assert.ThrowsAsync<SqlException>(() => runtime.ExecuteAsync("DELETE FROM dbo.pp_erp_config"));
        Assert.Equal(229, denied.Number);
        denied = await Assert.ThrowsAsync<SqlException>(() => runtime.ExecuteAsync("UPDATE dbo.pp_erp_config SET UserName=N'changed'"));
        Assert.Equal(229, denied.Number);
        Assert.Equal(metadata.Version, (await _db.Service.GetMetadataAsync())!.Version);
    }

    [SqlServerFact]
    public async Task FailedConnectionOrMissingSchemaDoesNotReplaceWorkingConfiguration()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var before = (await _db.Service.GetMetadataAsync())!;
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => _db.Service.SaveAsync(
            _db.Input("Definitely-wrong-password"), before.Version));
        await Assert.ThrowsAsync<ErpConfigurationException>(() => _db.Service.SaveAsync(
            new(_db.Server, "master", _db.SourceLogin, _db.SourcePassword), before.Version));
        Assert.Equal(before.Version, (await _db.Service.GetMetadataAsync())!.Version);
        await _db.Service.OpenSessionAsync();
    }

    [SqlServerFact]
    public async Task CorruptCipherFailsClosedAndReplacementPasswordCanRepairIt()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        await _db.Owner.ExecuteAsync("UPDATE dbo.pp_erp_config SET PasswordCipher = 0x00010203");
        var damaged = (await _db.Service.GetMetadataAsync())!;
        var identity = await _db.EncryptionIdentityAsync();
        var error = await Assert.ThrowsAsync<SqlException>(() => _db.Service.OpenSessionAsync());
        Assert.Equal(50103, error.Number);
        await Assert.ThrowsAsync<ErpConfigurationException>(() => _db.Service.SaveAsync(_db.Input(password: null), damaged.Version));
        await _db.ApplyConfigurationSchemaAsync();
        Assert.Equal(identity, await _db.EncryptionIdentityAsync());
        Assert.Equal(damaged.Version, (await _db.Service.GetMetadataAsync())!.Version);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
        await _db.Service.SaveAsync(_db.Input(), damaged.Version);
        await _db.Service.OpenSessionAsync();
    }

    [SqlServerFact]
    public async Task RestrictedRuntimeCanCompleteTheAppWorkflowWithoutLocalErpTables()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var snapshot = await _db.Service.OpenSessionAsync();
        var account = await _db.Owner.CreateAccountAsync();
        var accounts = new KontoRepository(snapshot.ConnectionFactory);
        var local = new EvidencijaConnectionFactory(_db.RuntimeConnectionString);
        var records = new PpAparatRepository(local, accounts);
        var reports = new IzvjestajEvidencijaRepository(local, accounts);
        await records.InsertAsync(RepositoryIntegrationTests.Item(account), "Runtime user");
        var group = Assert.Single(await reports.GetHistorijaAsync(account));
        Assert.Equal("Automated audit", group.NazivKupca);
        await reports.ZakljuciAsync(group.GrupaId, group.Verzija, "Runtime user");
        var closed = Assert.Single(await reports.GetHistorijaAsync(account));
        Assert.True(closed.Zakljucen);
        Assert.Single((await records.GetForReportAsync(closed.GrupaId, closed.Verzija)).Aparati);
    }

    [SqlServerFact]
    public async Task SavedSourceChangesOnlyNewSessionsAndExistingAuthenticationStaysOnItsSource()
    {
        await _db.ResetAsync();
        var alternative = new RepositoryDatabase();
        string? alternativeName = null;
        try
        {
            await alternative.InitializeAsync();
            alternativeName = new SqlConnectionStringBuilder(alternative.ErpConnectionString).InitialCatalog;
            await alternative.ExecuteErpAsync($"CREATE USER [{_db.SourceLogin}] FOR LOGIN [{_db.SourceLogin}]; GRANT SELECT ON dbo.a_user TO [{_db.SourceLogin}]; GRANT SELECT ON dbo.konta TO [{_db.SourceLogin}];");
            var username = "session_" + Guid.NewGuid().ToString("N")[..12];
            const string seed = "INSERT dbo.a_user (korime,ime,prezime,lozinka,kriploz,aktivan) VALUES (@username,@name,N'Test',N'User-test-password',N'',1);";
            await _db.Owner.ExecuteErpAsync(seed, new { username, name = "Original" });
            await alternative.ExecuteErpAsync(seed, new { username, name = "Alternative" });
            await _db.Service.SaveAsync(_db.Input(), null);
            var originalSession = await _db.Service.OpenSessionAsync();
            var metadata = (await _db.Service.GetMetadataAsync())!;
            await _db.Service.SaveAsync(new(_db.Server, alternativeName, _db.SourceLogin, _db.SourcePassword),
                metadata.Version);
            var newSession = await _db.Service.OpenSessionAsync();
            var oldUser = await new AuthRepository(originalSession.ConnectionFactory).AuthenticateAsync(username, "User-test-password");
            var newUser = await new AuthRepository(newSession.ConnectionFactory).AuthenticateAsync(username, "User-test-password");
            Assert.Equal("Original Test", oldUser!.PunoIme);
            Assert.Equal("Alternative Test", newUser!.PunoIme);
        }
        finally
        {
            if (alternativeName is not null) _db.ClearSourcePool(alternativeName);
            await alternative.DisposeAsync();
            await _db.ResetAsync();
        }
    }

    [SqlServerFact]
    public async Task UnavailablePrimaryGivesNoFallbackForLoginOrConfiguration()
    {
        var builder = new SqlConnectionStringBuilder(_db.RuntimeConnectionString) { InitialCatalog = "missing_" + Guid.NewGuid().ToString("N") };
        var unavailable = new ErpConfigurationService(new EvidencijaConnectionFactory(builder.ConnectionString));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => unavailable.OpenSessionAsync());
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => unavailable.GetMetadataAsync());
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => unavailable.TestAsync(_db.Input()));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => unavailable.SaveAsync(_db.Input(), null));
    }

    [SqlServerFact]
    public async Task UpgradeRemovesPasswordGatesAndPreservesExistingConfigurationAndLegacyAdminRows()
    {
        await _db.ResetAsync();
        await _db.Service.SaveAsync(_db.Input(), null);
        var metadata = (await _db.Service.GetMetadataAsync())!;
        var cipher = await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config");
        await _db.Owner.ExecuteAsync("""
            CREATE TABLE dbo.pp_config_admin
                (Id tinyint NOT NULL PRIMARY KEY, Salt varbinary(32) NOT NULL,
                 Iterations int NOT NULL, Verifier varbinary(32) NOT NULL);
            INSERT dbo.pp_config_admin VALUES (1, CRYPT_GEN_RANDOM(32), 600000, CRYPT_GEN_RANDOM(32));
            """);
        try
        {
            var legacy = await _db.Owner.ScalarAsync<string>("SELECT * FROM dbo.pp_config_admin FOR XML RAW, BINARY BASE64");
            foreach (var procedure in new[] { "pp_erp_admin_challenge", "pp_erp_admin_verify", "pp_erp_config_read_for_admin" })
                await _db.Owner.ExecuteAsync($"CREATE PROCEDURE dbo.{procedure} AS THROW 50101, 'Legacy gate', 1;");
            await _db.Owner.ExecuteAsync("""
                CREATE OR ALTER PROCEDURE dbo.pp_erp_config_metadata @Proof varbinary(8000)
                AS THROW 50101, 'Legacy gate', 1;
                """);
            await _db.Owner.ExecuteAsync("""
                CREATE OR ALTER PROCEDURE dbo.pp_erp_config_save @Proof varbinary(8000)
                AS THROW 50101, 'Legacy gate', 1;
                """);
            await _db.ApplyConfigurationSchemaAsync();

            Assert.Equal(legacy, await _db.Owner.ScalarAsync<string>("SELECT * FROM dbo.pp_config_admin FOR XML RAW, BINARY BASE64"));
            Assert.Equal(cipher, await _db.Owner.ScalarAsync<byte[]>("SELECT PasswordCipher FROM dbo.pp_erp_config"));
            Assert.Equal(metadata.Version, (await _db.Service.GetMetadataAsync())!.Version);
            Assert.Equal(0, await _db.Owner.ScalarAsync<int>("""
                SELECT COUNT(*) FROM sys.procedures
                WHERE name IN (N'pp_erp_admin_challenge', N'pp_erp_admin_verify', N'pp_erp_config_read_for_admin');
                """));
            await _db.Service.TestAsync(_db.Input());
            await _db.Service.SaveAsync(_db.Input(), metadata.Version);
            await _db.Service.OpenSessionAsync();
        }
        finally
        {
            await _db.Owner.ExecuteAsync("DROP TABLE dbo.pp_config_admin;");
            await _db.ApplyConfigurationSchemaAsync();
        }
    }
}

public sealed class ErpConfigurationDatabase : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private string _master = string.Empty;
    private readonly string _runtimePassword = "Test!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public RepositoryDatabase Owner { get; } = new();
    public string Server { get; private set; } = string.Empty;
    public string SourceLogin => "ppErpSourceTest_" + _suffix;
    private string RuntimeLogin => "ppErpClientTest_" + _suffix;
    public string SourcePassword { get; } = "Test!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string SourceDatabase { get; private set; } = string.Empty;
    public string RuntimeConnectionString { get; private set; } = string.Empty;
    public ErpConfigurationService Service { get; private set; } = null!;
    private bool _sourceLoginCreated, _runtimeLoginCreated;

    public async Task InitializeAsync()
    {
        Server = Environment.GetEnvironmentVariable("PPEVIDENCIJA_TEST_SQL_SERVER") ?? string.Empty;
        if (Server.Length == 0) return;
        var master = new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = "master",
            IntegratedSecurity = true, TrustServerCertificate = true };
        _master = master.ConnectionString;
        try
        {
            await Owner.InitializeAsync();
            SourceDatabase = new SqlConnectionStringBuilder(Owner.ErpConnectionString).InitialCatalog;
            await ApplyConfigurationSchemaAsync();
            await using var admin = new SqlConnection(_master);
            await admin.ExecuteAsync($"CREATE LOGIN [{SourceLogin}] WITH PASSWORD = '{SourcePassword}', CHECK_POLICY = OFF;");
            _sourceLoginCreated = true;
            await Owner.ExecuteErpAsync($"CREATE USER [{SourceLogin}] FOR LOGIN [{SourceLogin}]; GRANT SELECT ON dbo.a_user TO [{SourceLogin}]; GRANT SELECT ON dbo.konta TO [{SourceLogin}];");
            await admin.ExecuteAsync($"CREATE LOGIN [{RuntimeLogin}] WITH PASSWORD = '{_runtimePassword}', CHECK_POLICY = OFF;");
            _runtimeLoginCreated = true;
            await Owner.ExecuteAsync($"CREATE USER [{RuntimeLogin}] FOR LOGIN [{RuntimeLogin}]; ALTER ROLE ppEvidencija_runtime ADD MEMBER [{RuntimeLogin}];");
            var runtime = new SqlConnectionStringBuilder(Owner.ConnectionString)
            { IntegratedSecurity = false, UserID = RuntimeLogin, Password = _runtimePassword };
            RuntimeConnectionString = runtime.ConnectionString;
            Service = new ErpConfigurationService(new EvidencijaConnectionFactory(RuntimeConnectionString));
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task ApplyConfigurationSchemaAsync()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "Configure_erp.sql"));
        const string marker = "/* ERP_SCHEMA_BEGIN:";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Missing ERP schema boundary.");
        var end = script.IndexOf("*/", start + marker.Length, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Invalid ERP schema boundary.");
        foreach (var batch in Regex.Split(script[(end + 2)..], @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await Owner.ExecuteAsync(batch);
    }

    public Task ResetAsync() => Owner.ExecuteAsync("DELETE FROM dbo.pp_erp_config");

    internal Task<int> EncryptionObjectCountAsync() => Owner.ScalarAsync<int>("""
        SELECT (SELECT COUNT(*) FROM sys.symmetric_keys
                WHERE name IN (N'##MS_DatabaseMasterKey##', N'pp_ErpConfigurationKey'))
             + (SELECT COUNT(*) FROM sys.certificates WHERE name=N'pp_ErpConfigurationCertificate');
        """);

    internal Task<string> EncryptionIdentityAsync() => Owner.ScalarAsync<string>("""
        SELECT (SELECT name,key_guid,algorithm_desc FROM sys.symmetric_keys
                WHERE name IN (N'##MS_DatabaseMasterKey##', N'pp_ErpConfigurationKey')
                ORDER BY name FOR XML RAW)
             + (SELECT name,thumbprint FROM sys.certificates
                WHERE name=N'pp_ErpConfigurationCertificate' FOR XML RAW, BINARY BASE64);
        """);

    internal async Task RemoveEncryptionKeysAsync(bool includeMasterKey)
    {
        // This helper only operates on the generated disposable fixture database.
        // Never remove keys while a saved configuration still depends on them.
        Assert.Equal(0, await Owner.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.pp_erp_config"));
        await Owner.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name=N'pp_ErpConfigurationKey')
                DROP SYMMETRIC KEY pp_ErpConfigurationKey;
            IF EXISTS (SELECT 1 FROM sys.certificates WHERE name=N'pp_ErpConfigurationCertificate')
                DROP CERTIFICATE pp_ErpConfigurationCertificate;
            """);
        if (includeMasterKey)
            await Owner.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name=N'##MS_DatabaseMasterKey##')
                    DROP MASTER KEY;
                """);
    }

    public ErpConfigurationInput Input() => Input(SourcePassword);
    public ErpConfigurationInput Input(string? password) => new(Server, SourceDatabase, SourceLogin, password);

    public async Task DisposeAsync()
    {
        ClearSourcePool(SourceDatabase);
        ClearSourcePool("master");
        if (RuntimeConnectionString.Length > 0)
        {
            using var runtime = new SqlConnection(RuntimeConnectionString);
            SqlConnection.ClearPool(runtime);
        }
        await Owner.DisposeAsync();
        if (_master.Length == 0) return;
        await using var admin = new SqlConnection(_master);
        foreach (var name in new[] { SourceLogin, RuntimeLogin })
        {
            if (!Regex.IsMatch(name, "^ppErp(Source|Client)Test_[0-9a-f]{32}$")) throw new InvalidOperationException("Unsafe test login name.");
            if (name == SourceLogin ? _sourceLoginCreated : _runtimeLoginCreated)
                await admin.ExecuteAsync($"DROP LOGIN [{name}];");
        }
        _sourceLoginCreated = _runtimeLoginCreated = false;
    }

    internal void ClearSourcePool(string databaseName)
    {
        if (Server.Length == 0 || databaseName.Length == 0) return;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server, InitialCatalog = databaseName,
            UserID = SourceLogin, Password = SourcePassword,
            IntegratedSecurity = false, Encrypt = true, TrustServerCertificate = true,
            PersistSecurityInfo = false, ConnectTimeout = 8, ApplicationName = "ppEvidencija ERP"
        };
        using var connection = new SqlConnection(builder.ConnectionString);
        SqlConnection.ClearPool(connection);
    }
}
