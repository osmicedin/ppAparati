using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using PpEvidencija.Data;
using PpEvidencija.Models;
using Xunit;

namespace PpEvidencija.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPEVIDENCIJA_TEST_SQL_SERVER")))
        {
            Skip = "Set PPEVIDENCIJA_TEST_SQL_SERVER to a disposable SQL Server instance that permits database creation.";
        }
    }
}

public sealed class RepositoryIntegrationTests : IClassFixture<RepositoryDatabase>
{
    private readonly RepositoryDatabase _database;

    public RepositoryIntegrationTests(RepositoryDatabase database) => _database = database;

    [Theory]
    [InlineData(1899, 12, 31)]
    [InlineData(9999, 7, 1)]
    [InlineData(9999, 12, 31)]
    public async Task InvalidServiceDateIsRejectedBeforeOpeningAConnection(int year, int month, int day)
    {
        var repository = new PpAparatRepository(new SqlConnectionFactory("invalid connection string"));
        var input = Item("Unused", new DateTime(year, month, day));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.InsertAsync(input));
    }

    [SqlServerFact]
    public async Task SchemaCanBeAppliedAgainWithoutLosingData()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));

        await _database.ApplySchemaAsync();

        Assert.Single(await _database.Records.GetForReportAsync(account, 5, 2026));
    }

    [SqlServerFact]
    public async Task ClosingBlocksNewEntriesAndReopeningAllowsThemWithAuditHistory()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));

        await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Closer");
        var closed = await _database.ReadStatusAsync(account);
        Assert.True(closed.Zakljucen);
        Assert.Equal("Z", closed.Radnja);
        Assert.Equal("Closer", closed.Korisnik);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _database.Records.InsertAsync(Item(account)));
        Assert.Contains("zaključen", rejected.Message);
        Assert.Single(await _database.Records.GetForReportAsync(account, 5, 2026));

        await _database.Status.PromijeniStatusAsync(account, 5, 2026, true, false, "Reopener");
        await _database.Records.InsertAsync(Item(account));

        var opened = await _database.ReadStatusAsync(account);
        Assert.False(opened.Zakljucen);
        Assert.Equal("O", opened.Radnja);
        Assert.Equal("Reopener", opened.Korisnik);
        Assert.Equal(2, (await _database.Records.GetForReportAsync(account, 5, 2026)).Count);
        var audit = await _database.ReadAuditAsync(account);
        Assert.Equal(new[] { "Z", "O" }, audit.Select(row => row.Radnja));
        Assert.Equal(new[] { "Closer", "Reopener" }, audit.Select(row => row.Korisnik));
    }

    [SqlServerFact]
    public async Task ClosingIsIsolatedToSelectedAccountAndMonth()
    {
        var account = await _database.CreateAccountAsync();
        var other = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));
        await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Audit");

        await _database.Records.InsertAsync(Item(other));
        await _database.Records.InsertAsync(Item(account, new DateTime(2026, 6, 1)));
        await _database.Records.InsertAsync(Item(account, new DateTime(2025, 5, 31)));

        Assert.Single(await _database.Records.GetForReportAsync(other, 5, 2026));
        Assert.Single(await _database.Records.GetForReportAsync(account, 6, 2026));
        Assert.Single(await _database.Records.GetForReportAsync(account, 5, 2025));
    }

    [SqlServerFact]
    public async Task ConcurrentCloseRejectsTheStaleRequestWithoutDuplicatingAudit()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));

        async Task<Exception?> CloseAsync(string user)
        {
            try
            {
                await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, user);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var results = await Task.WhenAll(CloseAsync("First"), CloseAsync("Second"));

        Assert.Single(results, result => result is null);
        var rejected = Assert.IsType<InvalidOperationException>(Assert.Single(results, result => result is not null));
        Assert.Contains("međuvremenu", rejected.Message);
        Assert.Single(await _database.ReadAuditAsync(account));
        Assert.True((await _database.ReadStatusAsync(account)).Zakljucen);
    }

    [SqlServerFact]
    public async Task EmptyPeriodCannotBeClosedOrCreateAnAuditEntry()
    {
        var account = await _database.CreateAccountAsync();

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Audit"));

        Assert.Contains("više nema podataka", rejected.Message);
        Assert.Empty(await _database.ReadAuditAsync(account));
        Assert.Equal(0, await _database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ppaparati_status WHERE konto=@account", new { account }));
    }

    [SqlServerFact]
    public async Task AuditFailureRollsBackBothExistingStatusAndFirstStatusCreation()
    {
        var existing = await _database.CreateAccountAsync();
        var first = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(existing));
        await _database.Records.InsertAsync(Item(first));
        await _database.Status.PromijeniStatusAsync(existing, 5, 2026, false, true, "Audit");
        await _database.ExecuteAsync("""
            CREATE TRIGGER dbo.RejectTestAudit ON dbo.ppaparati_status_audit AFTER INSERT
            AS BEGIN THROW 51001, 'Injected audit failure', 1; END;
            """);
        try
        {
            var existingError = await Assert.ThrowsAsync<SqlException>(
                () => _database.Status.PromijeniStatusAsync(existing, 5, 2026, true, false, "Reopener"));
            var firstError = await Assert.ThrowsAsync<SqlException>(
                () => _database.Status.PromijeniStatusAsync(first, 5, 2026, false, true, "Closer"));

            Assert.Equal(51001, existingError.Number);
            Assert.Equal(51001, firstError.Number);
            Assert.True((await _database.ReadStatusAsync(existing)).Zakljucen);
            Assert.Single(await _database.ReadAuditAsync(existing));
            Assert.Empty(await _database.ReadAuditAsync(first));
            Assert.Equal(0, await _database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ppaparati_status WHERE konto=@first", new { first }));
        }
        finally
        {
            await _database.ExecuteAsync("DROP TRIGGER dbo.RejectTestAudit");
        }
    }

    [SqlServerFact]
    public async Task OverviewUsesSelectedPeriodForClosedAndAllButAllPeriodsForOpen()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));
        await _database.Records.InsertAsync(Item(account, new DateTime(2026, 6, 1)));
        await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Audit");

        var open = (await _database.Status.GetPregledAsync(5, 2026, IzvjestajStatusFilter.Nezakljuceni))
            .Where(row => row.Konto == account).ToList();
        var closed = (await _database.Status.GetPregledAsync(5, 2026, IzvjestajStatusFilter.Zakljuceni))
            .Where(row => row.Konto == account).ToList();
        var all = (await _database.Status.GetPregledAsync(5, 2026, IzvjestajStatusFilter.Svi))
            .Where(row => row.Konto == account).ToList();

        Assert.Equal(6, Assert.Single(open).Mjesec);
        Assert.Equal(5, Assert.Single(closed).Mjesec);
        Assert.True(Assert.Single(all).Zakljucen);
    }

    [SqlServerFact]
    public async Task ReportIncludesLastDayOfMonthAndExcludesNextMonth()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account, new DateTime(2024, 2, 29)));
        await _database.Records.InsertAsync(Item(account, new DateTime(2024, 3, 1)));

        var rows = await _database.Records.GetForReportAsync(account, 2, 2024);
        await _database.Status.PromijeniStatusAsync(account, 2, 2024, false, true, "Audit");
        var overview = (await _database.Status.GetPregledAsync(2, 2024, IzvjestajStatusFilter.Zakljuceni))
            .Where(row => row.Konto == account);

        Assert.Equal(new DateTime(2024, 2, 29), Assert.Single(rows).DatumServisa);
        Assert.Equal(1, Assert.Single(overview).BrojAparata);
    }

    [SqlServerFact]
    public async Task ServiceDateBoundsCanBeReportedAndClosedAndInvalidDatesWriteNothing()
    {
        var account = await _database.CreateAccountAsync();
        foreach (var date in new[] { ServiceDateRange.Minimum, ServiceDateRange.Maximum })
        {
            await _database.Records.InsertAsync(Item(account, date));
            var record = Assert.Single(await _database.Records.GetForReportAsync(account, date.Month, date.Year));
            Assert.Equal(date.AddMonths(6), record.SljedeciServis);
            await _database.Status.PromijeniStatusAsync(account, date.Month, date.Year, false, true, "Audit");
        }

        foreach (var date in new[] { ServiceDateRange.Minimum.AddDays(-1), ServiceDateRange.Maximum.AddDays(1) })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _database.Records.InsertAsync(Item(account, date)));
        }

        Assert.Equal(2, await _database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ppaparati WHERE konto=@account", new { account }));
        Assert.Equal(2, (await _database.ReadAuditAsync(account)).Count);
    }

    [SqlServerFact]
    public async Task LastCalendarMonthReturnsEmptyWithoutDateOverflow()
    {
        var account = await _database.CreateAccountAsync();

        Assert.Empty(await _database.Records.GetForReportAsync(account, 12, 9999));
        Assert.Empty(await _database.Status.GetPregledAsync(12, 9999, IzvjestajStatusFilter.Svi));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _database.Status.PromijeniStatusAsync(account, 12, 9999, false, true, "Audit"));
        Assert.Contains("više nema podataka", rejected.Message);
    }

    [SqlServerFact]
    public async Task ClosingWaitsForAnInFlightInsertThenRetainsTheInsertedRecord()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account));
        await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Setup");
        await _database.Status.PromijeniStatusAsync(account, 5, 2026, true, false, "Setup");
        await using var gateConnection = new SqlConnection(_database.ConnectionString);
        await gateConnection.OpenAsync();
        await using var gateTransaction = gateConnection.BeginTransaction(IsolationLevel.Serializable);
        await gateConnection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ppaparati WITH (TABLOCK,HOLDLOCK)", transaction: gateTransaction);
        Task<long>? insert = null;
        Task? close = null;
        try
        {
            insert = _database.Records.InsertAsync(Item(account));
            var observed = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                // The disposable test instance must permit reading lock metadata
                // (VIEW SERVER STATE, or VIEW SERVER PERFORMANCE STATE on newer SQL Server versions).
                if (await _database.ScalarAsync<int>("""
                    SELECT COUNT(*) FROM sys.dm_tran_locks
                    WHERE resource_database_id=DB_ID()
                      AND resource_type='OBJECT'
                      AND resource_associated_entity_id=OBJECT_ID('dbo.ppaparati')
                      AND request_status='WAIT'
                    """) > 0)
                {
                    observed = true;
                    break;
                }
                await Task.Delay(10);
            }

            Assert.True(observed, "The insert did not reach the controlled table lock.");
            Assert.False(insert.IsCompleted);
            close = _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Audit");
            await Task.Delay(100);
            Assert.False(close.IsCompleted);
            await gateTransaction.CommitAsync();
            await Task.WhenAll(insert, close);

            Assert.Equal(2, (await _database.Records.GetForReportAsync(account, 5, 2026)).Count);
            Assert.True((await _database.ReadStatusAsync(account)).Zakljucen);
            Assert.Equal(3, (await _database.ReadAuditAsync(account)).Count);
        }
        finally
        {
            if (gateTransaction.Connection is not null)
            {
                await gateTransaction.RollbackAsync();
            }
            if (insert is not null)
            {
                await insert;
            }
            if (close is not null)
            {
                await close;
            }
        }
    }

    [SqlServerFact]
    public async Task ConcurrentInsertAndClosePreserveStatusAndRecordCount()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var account = await _database.CreateAccountAsync();
            await _database.Records.InsertAsync(Item(account));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var insert = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await _database.Records.InsertAsync(Item(account));
                    return true;
                }
                catch (InvalidOperationException error) when (error.Message.Contains("zaključen"))
                {
                    return false;
                }
            });
            var close = Task.Run(async () =>
            {
                await gate.Task;
                await _database.Status.PromijeniStatusAsync(account, 5, 2026, false, true, "Audit");
            });

            gate.SetResult();
            await Task.WhenAll(insert, close);

            Assert.Equal(await insert ? 2 : 1, (await _database.Records.GetForReportAsync(account, 5, 2026)).Count);
            Assert.True((await _database.ReadStatusAsync(account)).Zakljucen);
            Assert.Single(await _database.ReadAuditAsync(account));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _database.Records.InsertAsync(Item(account)));
        }
    }

    private static PpAparatInput Item(string account, DateTime? date = null) => new(
        account, "S", 6m, Guid.NewGuid().ToString("N"), 2020, date ?? new DateTime(2026, 5, 15),
        "Ispravan", "N/A", "Automated test");
}

public sealed class RepositoryDatabase : IAsyncLifetime
{
    private readonly string _name = "ppEvidencijaAudit_" + Guid.NewGuid().ToString("N");
    private string? _masterConnectionString;
    private bool _created;

    public string ConnectionString { get; private set; } = string.Empty;
    public PpAparatRepository Records { get; private set; } = null!;
    public IzvjestajEvidencijaRepository Status { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("PPEVIDENCIJA_TEST_SQL_SERVER");
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        EnsureDisposableName();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 5
        };
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _name;
        ConnectionString = builder.ConnectionString;
        try
        {
            await using (var master = new SqlConnection(_masterConnectionString))
            {
                await master.ExecuteAsync($"CREATE DATABASE [{_name}]");
                _created = true;
            }
            await ApplySchemaAsync();
            await ExecuteAsync("CREATE TABLE dbo.konta (konto nvarchar(20) NOT NULL PRIMARY KEY, naziv nvarchar(255) NOT NULL)");
            var factory = new SqlConnectionFactory(ConnectionString);
            Records = new PpAparatRepository(factory);
            Status = new IzvjestajEvidencijaRepository(factory);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (!_created)
        {
            return;
        }
        EnsureDisposableName();
        await using (var database = new SqlConnection(ConnectionString))
        {
            SqlConnection.ClearPool(database);
        }
        await using var master = new SqlConnection(_masterConnectionString);
        await master.ExecuteAsync($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
        _created = false;
    }

    public async Task ApplySchemaAsync()
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "Create_ppaparati.sql"));
        foreach (var batch in Regex.Split(schema, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                await ExecuteAsync(batch);
            }
        }
    }

    public async Task<string> CreateAccountAsync()
    {
        var account = "T" + Guid.NewGuid().ToString("N")[..18];
        await ExecuteAsync("INSERT dbo.konta (konto,naziv) VALUES (@account,N'Automated audit')", new { account });
        return account;
    }

    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteAsync(sql, parameters);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    public async Task<StatusRow> ReadStatusAsync(string account)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<StatusRow>("""
            SELECT zakljucen AS Zakljucen, posljednja_radnja AS Radnja, promijenio_korisnik AS Korisnik
            FROM dbo.ppaparati_status WHERE konto=@account AND godina=2026 AND mjesec=5
            """, new { account });
    }

    public async Task<List<AuditRow>> ReadAuditAsync(string account)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.QueryAsync<AuditRow>("""
            SELECT a.radnja AS Radnja, a.korisnicko_ime AS Korisnik
            FROM dbo.ppaparati_status_audit a
            INNER JOIN dbo.ppaparati_status s ON s.id=a.status_id
            WHERE s.konto=@account ORDER BY a.id
            """, new { account })).AsList();
    }

    private void EnsureDisposableName()
    {
        if (!Regex.IsMatch(_name, "^ppEvidencijaAudit_[0-9a-f]{32}$"))
        {
            throw new InvalidOperationException("Refusing to create or remove a database outside the generated audit name.");
        }
    }

    public sealed record StatusRow(bool Zakljucen, string Radnja, string Korisnik);
    public sealed record AuditRow(string Radnja, string Korisnik);
}
