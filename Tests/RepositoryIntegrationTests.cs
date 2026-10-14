using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using DocumentFormat.OpenXml.Packaging;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;
using Xunit;

namespace PpEvidencija.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPEVIDENCIJA_TEST_SQL_SERVER")))
            Skip = "Set PPEVIDENCIJA_TEST_SQL_SERVER to a disposable SQL Server instance that permits database creation.";
    }
}

public sealed class SchemaScriptTests
{
    [Fact]
    public void SchemaExtractionExcludesDatabaseCreationAndSelection()
    {
        const string script = """
            IF DB_ID(N'evidencija') IS NULL CREATE DATABASE evidencija;
            GO
            USE evidencija;
            GO
            /* SCHEMA_BEGIN: schema for the selected database. */
            SET NOCOUNT ON;
            SELECT DB_NAME();
            """;

        Assert.Equal("SET NOCOUNT ON;\nSELECT DB_NAME();",
            RepositoryDatabase.ExtractSchemaBody(script).Trim().Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("USE evidencija; SELECT DB_NAME();")]
    [InlineData("USE evidencija; /* SCHEMA_BEGIN: unterminated boundary")]
    public void SchemaExtractionRefusesScriptsWithoutACompleteBoundary(string script)
    {
        Assert.Throws<InvalidOperationException>(() => RepositoryDatabase.ExtractSchemaBody(script));
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
        var repository = new PpAparatRepository(new EvidencijaConnectionFactory("invalid connection string"),
            new KontoRepository(new ErpConnectionFactory("invalid connection string")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.InsertAsync(Item("Unused", new DateTime(year, month, day)), "Test"));
    }

    [SqlServerFact]
    public async Task SeparateErpSupportsLoginAndAccountLookupWithoutLocalCopiesAndSurvivesSchemaRerun()
    {
        Assert.Equal(5, await _database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN
                (N'a_user', N'konta', N'ppaparati', N'ppaparati_grupe',
                 N'ppaparati_grupe_audit', N'ppaparati_status', N'ppaparati_status_audit');
            """));
        Assert.Equal(0, await _database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'a_user', N'konta');"));

        var account = await _database.CreateAccountAsync();
        var username = "schema_" + Guid.NewGuid().ToString("N")[..16];
        var password = Guid.NewGuid().ToString("N");
        var legacyPassword = Guid.NewGuid().ToString("N");
        await _database.ExecuteErpAsync("""
            INSERT dbo.a_user (korime, ime, prezime, lozinka, kriploz, aktivan)
            VALUES (@username, N'Schema', N'Test', @password, @legacyPassword, 1);
            """, new { username, password, legacyPassword });
        var factory = new ErpConnectionFactory(_database.ErpConnectionString);
        var users = new AuthRepository(factory);
        var accounts = new KontoRepository(factory);

        var authenticated = await users.AuthenticateAsync(" " + username.ToUpperInvariant() + " ", password);
        Assert.NotNull(authenticated);
        Assert.Equal(username, authenticated.KorisnickoIme);
        Assert.Equal("Schema Test", authenticated.PunoIme);
        Assert.Equal(authenticated, await users.AuthenticateAsync(username, legacyPassword));
        Assert.Null(await users.AuthenticateAsync(username, "incorrect password"));
        Assert.Null(await users.AuthenticateAsync(username, ""));
        await _database.ExecuteErpAsync("UPDATE dbo.a_user SET aktivan=0 WHERE korime=@username", new { username });
        Assert.Null(await users.AuthenticateAsync(username, password));
        await _database.ExecuteErpAsync("UPDATE dbo.a_user SET aktivan=1 WHERE korime=@username", new { username });
        var found = Assert.Single(await accounts.SearchAsync(account));
        Assert.Equal(account, found.Sifra);
        Assert.Equal("Automated audit", found.Naziv);

        const string lookupSnapshotSql = """
            SELECT (SELECT korime, ime, prezime, lozinka, kriploz, aktivan
                    FROM dbo.a_user WHERE korime = @username FOR XML RAW)
                 + (SELECT konto, naziv FROM dbo.konta WHERE konto = @account FOR XML RAW);
            """;
        var before = await _database.ScalarErpAsync<string>(lookupSnapshotSql, new { username, account });
        await _database.ApplySchemaAsync();
        Assert.Equal(before, await _database.ScalarErpAsync<string>(lookupSnapshotSql, new { username, account }));
        Assert.Equal(authenticated, await users.AuthenticateAsync(username, password));
        Assert.Equal(found, Assert.Single(await accounts.SearchAsync(account)));
    }

    [Theory]
    [InlineData("2100001")]
    [InlineData("2120001")]
    [InlineData("A211001")]
    public async Task NonCustomerAccountsAreRejectedBeforeOpeningEitherConnection(string account)
    {
        var accounts = new KontoRepository(new ErpConnectionFactory("invalid connection string"));
        var records = new PpAparatRepository(new EvidencijaConnectionFactory("invalid connection string"), accounts);
        Assert.False(await accounts.ExistsAsync(account));
        Assert.Empty(await accounts.GetNamesAsync([account]));
        await Assert.ThrowsAsync<ArgumentException>(() => records.InsertAsync(Item(account), "Creator"));
    }

    [SqlServerFact]
    public async Task SearchExistenceAndNamesExcludeNon211AccountsAndSearchTreatsWildcardsLiterally()
    {
        var account = await _database.CreateAccountAsync();
        var excluded = "212" + account[3..];
        var name = "Search_%[~" + Guid.NewGuid().ToString("N");
        await _database.ExecuteErpAsync("""
            UPDATE dbo.konta SET naziv=@name WHERE konto=@account;
            INSERT dbo.konta (konto,naziv) VALUES (@excluded,@name), (@account,N'ZZ Duplicate name');
            """, new { account, excluded, name });
        var found = Assert.Single(await _database.Accounts.SearchAsync(name));
        Assert.Equal(account, found.Sifra);
        Assert.True(await _database.Accounts.ExistsAsync(" " + account + " "));
        Assert.False(await _database.Accounts.ExistsAsync(excluded));
        var names = await _database.Accounts.GetNamesAsync([account, excluded, account.ToUpperInvariant()]);
        Assert.Equal(name, names[account]);
        Assert.Equal(name, names[account.ToUpperInvariant()]);
        Assert.DoesNotContain(excluded, names.Keys);
        var before = await _database.SnapshotAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _database.Records.InsertAsync(Item(excluded), "Creator"));
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    [SqlServerFact]
    public async Task BatchedNameLookupSupportsMoreThanTheSqlParameterLimit()
    {
        var prefix = "211" + Guid.NewGuid().ToString("N")[..11];
        var accounts = Enumerable.Range(0, 2101).Select(index => prefix + index.ToString("D4")).ToArray();
        await _database.ExecuteErpAsync("INSERT dbo.konta (konto,naziv) VALUES (@account,@name)",
            accounts.Select(account => new { account, name = "Kupac " + account }));
        var names = await _database.Accounts.GetNamesAsync(accounts);
        Assert.Equal(accounts.Length, names.Count);
        Assert.All(accounts, account => Assert.Equal("Kupac " + account, names[account]));
    }

    [SqlServerFact]
    public async Task OverviewHistoryAndDocxUseErpNamesAndMissingAccountPreservesRecords()
    {
        var account = await _database.CreateAccountAsync();
        const string name = "ERP Kupac Čelik";
        await _database.ExecuteErpAsync("UPDATE dbo.konta SET naziv=@name WHERE konto=@account", new { name, account });
        await _database.Records.InsertAsync(Item(account), "Creator");
        var current = await CurrentAsync(account);
        Assert.Equal(name, current.NazivKupca);
        Assert.Equal(name, Assert.Single(await _database.Status.GetHistorijaAsync(account)).NazivKupca);
        var report = await _database.Records.GetForReportAsync(current.GrupaId, current.Verzija);
        Assert.Equal(name, report.Grupa.NazivKupca);
        var path = Path.Combine(Path.GetTempPath(), $"ppEvidencija-erp-test-{Guid.NewGuid():N}.docx");
        try
        {
            await new DocxReportService().GenerateAsync(
                new("ERP test", new DateTime(2026, 5, 20), report.Grupa.Kupac, current.GrupaId, report.Aparati), path);
            using var document = WordprocessingDocument.Open(path, false);
            Assert.Contains(name, document.MainDocumentPart!.Document!.InnerText);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        await _database.Status.ZakljuciAsync(current.GrupaId, current.Verzija, "Closer");
        var before = await _database.SnapshotAsync();
        await _database.ExecuteErpAsync("DELETE dbo.konta WHERE konto=@account", new { account });
        var retained = Assert.Single(await _database.Status.GetHistorijaAsync(account));
        Assert.True(retained.Zakljucen);
        Assert.Equal(account, retained.Konto);
        Assert.Equal(KontoRepository.MissingAccountName, retained.NazivKupca);
        Assert.Equal(KontoRepository.MissingAccountName, (await CurrentAsync(account)).NazivKupca);
        var missingReport = await _database.Records.GetForReportAsync(retained.GrupaId, retained.Verzija);
        Assert.Equal(KontoRepository.MissingAccountName, missingReport.Grupa.NazivKupca);
        Assert.Single(missingReport.Aparati);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.Records.InsertAsync(Item(account), "Creator"));
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    [SqlServerFact]
    public async Task UnavailableErpCannotCreateLocalRecordsOrFallBackForReportsOrLogin()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        var group = await CurrentAsync(account);
        var builder = new SqlConnectionStringBuilder(_database.ErpConnectionString)
        {
            InitialCatalog = "ppEvidencijaMissing_" + Guid.NewGuid().ToString("N"), Pooling = false
        };
        var factory = new ErpConnectionFactory(builder.ConnectionString);
        var accounts = new KontoRepository(factory);
        var primary = new EvidencijaConnectionFactory(_database.ConnectionString);
        var records = new PpAparatRepository(primary, accounts);
        var reports = new IzvjestajEvidencijaRepository(primary, accounts);
        var before = await _database.SnapshotAsync();
        var error = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => records.InsertAsync(Item(account), "Creator"));
        Assert.Contains("ERP baza", error.Message);
        Assert.DoesNotContain(builder.InitialCatalog, error.Message);
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => records.GetForReportAsync(group.GrupaId, group.Verzija));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => reports.GetHistorijaAsync(account));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => reports.GetPregledAsync(IzvjestajStatusFilter.Svi));
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => new AuthRepository(factory).AuthenticateAsync("test", "test"));
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    [SqlServerFact]
    public async Task UnavailableEvidencijaAndCancelledErpDoNotWriteRecords()
    {
        var account = await _database.CreateAccountAsync();
        var before = await _database.SnapshotAsync();
        var builder = new SqlConnectionStringBuilder(_database.ConnectionString)
        {
            InitialCatalog = "ppEvidencijaMissing_" + Guid.NewGuid().ToString("N"), Pooling = false
        };
        var records = new PpAparatRepository(new EvidencijaConnectionFactory(builder.ConnectionString), _database.Accounts);
        var error = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => records.InsertAsync(Item(account), "Creator"));
        Assert.DoesNotContain(builder.InitialCatalog, error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _database.Accounts.SearchAsync(account, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _database.Records.InsertAsync(Item(account), "Creator", cancellation.Token));
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    [SqlServerFact]
    public async Task ErpQueryFailuresAreSanitizedAndDoNotBecomeMissingAccounts()
    {
        var account = await _database.CreateAccountAsync();
        var before = await _database.SnapshotAsync();
        await _database.ExecuteErpAsync("EXEC sys.sp_rename N'dbo.konta', N'konta_test_unavailable';");
        try
        {
            var error = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => _database.Accounts.GetNamesAsync([account]));
            Assert.Contains("ERP baza", error.Message);
            await Assert.ThrowsAsync<DatabaseUnavailableException>(() => _database.Accounts.SearchAsync(account));
            await Assert.ThrowsAsync<DatabaseUnavailableException>(() => _database.Records.InsertAsync(Item(account), "Creator"));
            Assert.Equal(before, await _database.SnapshotAsync());
        }
        finally { await _database.ExecuteErpAsync("EXEC sys.sp_rename N'dbo.konta_test_unavailable', N'konta';"); }
    }

    [SqlServerFact]
    public async Task SchemaCanBeAppliedAgainWithoutChangingGroupsOrTheirMembership()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        var original = await CurrentAsync(account);
        await _database.Status.ZakljuciAsync(original.GrupaId, original.Verzija, "Closer");
        await _database.Records.InsertAsync(Item(account, new DateTime(2025, 1, 1)), "Next");
        var before = await _database.SnapshotAsync();
        await _database.ApplySchemaAsync();
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    [SqlServerFact]
    public async Task MultipleMonthsAndYearsBelongToOneOpenGroupWithOneAccountRow()
    {
        var account = await _database.CreateAccountAsync();
        var dates = new[] { new DateTime(2024, 2, 29), new DateTime(2024, 3, 1), new DateTime(2026, 5, 31) };
        foreach (var date in dates) await _database.Records.InsertAsync(Item(account, date), "Creator");
        var current = await CurrentAsync(account);
        var report = await _database.Records.GetForReportAsync(current.GrupaId, current.Verzija);
        Assert.False(current.Zakljucen);
        Assert.Equal(3, current.BrojAparata);
        Assert.Equal(dates[0], current.DatumOd);
        Assert.Equal(dates[^1], current.DatumDo);
        Assert.Equal(dates, report.Aparati.Select(row => row.DatumServisa));
        Assert.Equal(current.GrupaId, report.Grupa.GrupaId);
        Assert.Single(await _database.Status.GetHistorijaAsync(account));
        var audit = Assert.Single(await _database.ReadAuditAsync(account));
        Assert.Equal("O", audit.Radnja);
        Assert.Equal("Creator", audit.Korisnik);
    }

    [SqlServerFact]
    public async Task ClosingThenBackdatedInsertCreatesNewGroupAndPreservesClosedReport()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        var initial = await CurrentAsync(account);
        await _database.Status.ZakljuciAsync(initial.GrupaId, initial.Verzija, "Closer");
        var closed = await CurrentAsync(account);
        var closedReport = await _database.Records.GetForReportAsync(closed.GrupaId, closed.Verzija);
        await _database.Records.InsertAsync(Item(account, new DateTime(2023, 1, 2)), "Next user");
        var opened = await CurrentAsync(account);
        var newReport = await _database.Records.GetForReportAsync(opened.GrupaId, opened.Verzija);
        Assert.NotEqual(closed.GrupaId, opened.GrupaId);
        Assert.False(opened.Zakljucen);
        Assert.True(closed.Zakljucen);
        Assert.NotNull(closed.ZakljucenoUtc);
        Assert.Equal(new DateTime(2023, 1, 2), Assert.Single(newReport.Aparati).DatumServisa);
        var unchanged = await _database.Records.GetForReportAsync(closed.GrupaId, closed.Verzija);
        Assert.Equal(Assert.Single(closedReport.Aparati).Id, Assert.Single(unchanged.Aparati).Id);
        Assert.Equal(new[] { opened.GrupaId, closed.GrupaId },
            (await _database.Status.GetHistorijaAsync(account)).Select(row => row.GrupaId));
        var audit = await _database.ReadAuditAsync(account);
        Assert.Equal(new[] { "O", "Z", "O" }, audit.Select(row => row.Radnja));
        Assert.Equal(new[] { "Creator", "Closer", "Next user" }, audit.Select(row => row.Korisnik));
    }

    [SqlServerFact]
    public async Task StatusFiltersUseCurrentAccountStateAndExcludeAccountsWithoutRecords()
    {
        var opened = await _database.CreateAccountAsync();
        var closed = await _database.CreateAccountAsync();
        var empty = await _database.CreateAccountAsync();
        foreach (var account in new[] { opened, closed })
        {
            await _database.Records.InsertAsync(Item(account), "Creator");
            var group = await CurrentAsync(account);
            await _database.Status.ZakljuciAsync(group.GrupaId, group.Verzija, "Closer");
        }
        await _database.Records.InsertAsync(Item(opened, new DateTime(2025, 1, 1)), "Next");
        var accounts = new[] { opened, closed, empty };
        var openRows = (await _database.Status.GetPregledAsync(IzvjestajStatusFilter.Nezakljuceni))
            .Where(row => accounts.Contains(row.Konto)).ToList();
        var closedRows = (await _database.Status.GetPregledAsync(IzvjestajStatusFilter.Zakljuceni))
            .Where(row => accounts.Contains(row.Konto)).ToList();
        var all = (await _database.Status.GetPregledAsync(IzvjestajStatusFilter.Svi))
            .Where(row => accounts.Contains(row.Konto)).ToList();
        Assert.Equal(opened, Assert.Single(openRows).Konto);
        Assert.Equal(closed, Assert.Single(closedRows).Konto);
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all.Single(row => row.Konto == opened).BrojAparata);
        Assert.DoesNotContain(all, row => row.Konto == empty);
    }

    [SqlServerFact]
    public async Task StaleReadAndCloseAreRejectedAfterAnotherInsertWithoutChangingAudit()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        var old = await CurrentAsync(account);
        await _database.Records.InsertAsync(Item(account), "Adder");
        await Assert.ThrowsAsync<IzvjestajPromijenjenException>(
            () => _database.Records.GetForReportAsync(old.GrupaId, old.Verzija));
        await Assert.ThrowsAsync<IzvjestajPromijenjenException>(
            () => _database.Status.ZakljuciAsync(old.GrupaId, old.Verzija, "Stale closer"));
        var current = await CurrentAsync(account);
        Assert.Equal(old.Verzija + 1, current.Verzija);
        Assert.False(current.Zakljucen);
        Assert.Equal(2, current.BrojAparata);
        Assert.Single(await _database.ReadAuditAsync(account));
    }

    [SqlServerFact]
    public async Task ConcurrentClosuresAllowExactlyOneAndRejectAStaleRead()
    {
        var account = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        var group = await CurrentAsync(account);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> CloseAsync(string user)
        {
            await gate.Task;
            return await Record.ExceptionAsync(() => _database.Status.ZakljuciAsync(group.GrupaId, group.Verzija, user));
        }
        var first = CloseAsync("First");
        var second = CloseAsync("Second");
        gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result is null);
        Assert.IsType<IzvjestajPromijenjenException>(Assert.Single(results, result => result is not null));
        Assert.Equal(new[] { "O", "Z" }, (await _database.ReadAuditAsync(account)).Select(row => row.Radnja));
        Assert.True((await CurrentAsync(account)).Zakljucen);
        await Assert.ThrowsAsync<IzvjestajPromijenjenException>(
            () => _database.Records.GetForReportAsync(group.GrupaId, group.Verzija));
    }

    [SqlServerFact]
    public async Task ConcurrentFirstInsertsCreateExactlyOneOpenGroup()
    {
        var account = await _database.CreateAccountAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inserts = Enumerable.Range(0, 8).Select(async number =>
        {
            await gate.Task;
            return await _database.Records.InsertAsync(Item(account, new DateTime(2020 + number, 1, 1)), "Creator");
        }).ToArray();
        gate.SetResult();
        await Task.WhenAll(inserts);
        var group = Assert.Single(await _database.Status.GetHistorijaAsync(account));
        Assert.False(group.Zakljucen);
        Assert.Equal(8, group.BrojAparata);
        Assert.Equal(8, (await _database.Records.GetForReportAsync(group.GrupaId, group.Verzija)).Aparati.Count);
        Assert.Single(await _database.ReadAuditAsync(account));
    }

    [SqlServerFact]
    public async Task ConcurrentInsertAndCloseEitherRejectStaleCloseOrStartANewOpenGroup()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var account = await _database.CreateAccountAsync();
            await _database.Records.InsertAsync(Item(account), "Creator");
            var old = await CurrentAsync(account);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var insert = Task.Run(async () =>
            {
                await gate.Task;
                return await _database.Records.InsertAsync(Item(account), "Adder");
            });
            var close = Task.Run(async () =>
            {
                await gate.Task;
                return await Record.ExceptionAsync(() => _database.Status.ZakljuciAsync(old.GrupaId, old.Verzija, "Closer"));
            });
            gate.SetResult();
            await Task.WhenAll(insert, close);
            var history = await _database.Status.GetHistorijaAsync(account);
            var current = await CurrentAsync(account);
            Assert.Equal(2, history.Sum(row => row.BrojAparata));
            Assert.False(current.Zakljucen);
            Assert.Single(history, row => !row.Zakljucen);
            if (await close is null)
            {
                Assert.Equal(2, history.Count);
                Assert.Equal(1, current.BrojAparata);
                Assert.NotEqual(old.GrupaId, current.GrupaId);
                Assert.Equal(new[] { "O", "Z", "O" }, (await _database.ReadAuditAsync(account)).Select(row => row.Radnja));
            }
            else
            {
                Assert.IsType<IzvjestajPromijenjenException>(await close);
                Assert.Single(history);
                Assert.Equal(old.GrupaId, current.GrupaId);
                Assert.Equal(2, current.BrojAparata);
                Assert.Single(await _database.ReadAuditAsync(account));
            }
        }
    }

    [SqlServerFact]
    public async Task ConcurrentSnapshotAndInsertNeverMixVersionsAndRecords()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var account = await _database.CreateAccountAsync();
            await _database.Records.InsertAsync(Item(account), "Creator");
            var group = await CurrentAsync(account);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var snapshot = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    return await _database.Records.GetForReportAsync(group.GrupaId, group.Verzija);
                }
                catch (IzvjestajPromijenjenException) { return null; }
            });
            var insert = Task.Run(async () =>
            {
                await gate.Task;
                await _database.Records.InsertAsync(Item(account), "Adder");
            });
            gate.SetResult();
            await Task.WhenAll(snapshot, insert);
            if (await snapshot is { } report)
            {
                Assert.Equal(group.Verzija, report.Grupa.Verzija);
                Assert.Equal(1, report.Grupa.BrojAparata);
                Assert.Single(report.Aparati);
            }
            Assert.Equal(2, (await CurrentAsync(account)).BrojAparata);
        }
    }

    [SqlServerFact]
    public async Task AuditFailureRollsBackClosingAndNewGroupWithItsRecord()
    {
        var existing = await _database.CreateAccountAsync();
        var first = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(existing), "Creator");
        var group = await CurrentAsync(existing);
        var before = await _database.SnapshotAsync();
        await _database.ExecuteAsync("""
            CREATE TRIGGER dbo.RejectTestAudit ON dbo.ppaparati_grupe_audit AFTER INSERT
            AS BEGIN THROW 51001, 'Injected audit failure', 1; END;
            """);
        try
        {
            Assert.Equal(51001, (await Assert.ThrowsAsync<SqlException>(
                () => _database.Status.ZakljuciAsync(group.GrupaId, group.Verzija, "Closer"))).Number);
            Assert.Equal(51001, (await Assert.ThrowsAsync<SqlException>(
                () => _database.Records.InsertAsync(Item(first), "Creator"))).Number);
            Assert.Equal(before, await _database.SnapshotAsync());
        }
        finally { await _database.ExecuteAsync("DROP TRIGGER dbo.RejectTestAudit"); }
    }

    [SqlServerFact]
    public async Task RecordFailureRollsBackNewGroupAuditAndExistingGroupVersion()
    {
        var existing = await _database.CreateAccountAsync();
        var first = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(existing), "Creator");
        var before = await _database.SnapshotAsync();
        await _database.ExecuteAsync("ALTER TABLE dbo.ppaparati ADD CONSTRAINT RejectTestRecord CHECK (tip <> N'Rejected by test')");
        try
        {
            foreach (var account in new[] { existing, first })
                Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(
                    () => _database.Records.InsertAsync(Item(account) with { Tip = "Rejected by test" }, "Creator"))).Number);
            Assert.Equal(before, await _database.SnapshotAsync());
        }
        finally { await _database.ExecuteAsync("ALTER TABLE dbo.ppaparati DROP CONSTRAINT RejectTestRecord"); }
    }

    [SqlServerFact]
    public async Task DatabaseEnforcesGroupAccountMembershipAndOnlyOneOpenGroup()
    {
        var account = await _database.CreateAccountAsync();
        var other = await _database.CreateAccountAsync();
        await _database.Records.InsertAsync(Item(account), "Creator");
        await _database.Records.InsertAsync(Item(other), "Creator");
        var group = await CurrentAsync(account);
        await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsync(
            "UPDATE dbo.ppaparati SET grupa_id=@groupId WHERE konto=@other", new { groupId = group.GrupaId, other }));
        await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsync(
            "INSERT dbo.ppaparati_grupe (konto,zakljucen,verzija) VALUES (@account,0,1)", new { account }));
        await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsync(
            "UPDATE dbo.ppaparati SET grupa_id=NULL WHERE konto=@account", new { account }));
    }

    [SqlServerFact]
    public async Task ServiceDateBoundsAreSupportedAndInvalidDatesWriteNothing()
    {
        var account = await _database.CreateAccountAsync();
        foreach (var date in new[] { ServiceDateRange.Minimum, ServiceDateRange.Maximum })
            await _database.Records.InsertAsync(Item(account, date), "Creator");
        var group = await CurrentAsync(account);
        var report = await _database.Records.GetForReportAsync(group.GrupaId, group.Verzija);
        Assert.Equal(2, report.Aparati.Count);
        Assert.All(report.Aparati, record => Assert.Equal(record.DatumServisa.AddMonths(6), record.SljedeciServis));
        await _database.Status.ZakljuciAsync(group.GrupaId, group.Verzija, "Closer");
        var before = await _database.SnapshotAsync();
        foreach (var date in new[] { ServiceDateRange.Minimum.AddDays(-1), ServiceDateRange.Maximum.AddDays(1) })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => _database.Records.InsertAsync(Item(account, date), "Creator"));
        Assert.Equal(before, await _database.SnapshotAsync());
    }

    private async Task<IzvjestajPregledRed> CurrentAsync(string account) =>
        Assert.Single(await _database.Status.GetPregledAsync(IzvjestajStatusFilter.Svi), row => row.Konto == account);

    internal static PpAparatInput Item(string account, DateTime? date = null) => new(
        account, "S", 6m, Guid.NewGuid().ToString("N"), 2020, date ?? new DateTime(2026, 5, 15),
        "Ispravan", "N/A", "Automated test");
}

public sealed class RepositoryDatabase : IAsyncLifetime
{
    private readonly string _name = "ppEvidencijaAudit_" + Guid.NewGuid().ToString("N");
    private readonly string _erpName = "ppEvidencijaErpAudit_" + Guid.NewGuid().ToString("N");
    private string? _masterConnectionString;
    private bool _created;
    private bool _erpCreated;
    public bool LegacyOnly { get; init; }
    public string ConnectionString { get; private set; } = string.Empty;
    public string ErpConnectionString { get; private set; } = string.Empty;
    public KontoRepository Accounts { get; private set; } = null!;
    public PpAparatRepository Records { get; private set; } = null!;
    public IzvjestajEvidencijaRepository Status { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("PPEVIDENCIJA_TEST_SQL_SERVER");
        if (string.IsNullOrWhiteSpace(server)) return;
        EnsureDisposableName();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = "master", IntegratedSecurity = true,
            TrustServerCertificate = true, ConnectTimeout = 5
        };
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _name;
        ConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _erpName;
        ErpConnectionString = builder.ConnectionString;
        try
        {
            await using (var master = new SqlConnection(_masterConnectionString))
            {
                await master.ExecuteAsync($"CREATE DATABASE [{_name}]");
                _created = true;
                await master.ExecuteAsync($"CREATE DATABASE [{_erpName}]");
                _erpCreated = true;
            }
            await ExecuteErpAsync("""
                CREATE TABLE dbo.a_user
                (
                    korime nvarchar(100) NOT NULL,
                    ime nvarchar(100) NULL,
                    prezime nvarchar(100) NULL,
                    lozinka nvarchar(200) NULL,
                    kriploz nvarchar(200) NULL,
                    aktivan nvarchar(20) NULL
                );
                CREATE TABLE dbo.konta (konto nvarchar(20) NOT NULL, naziv nvarchar(255) NULL);
                """);
            if (LegacyOnly)
            {
                var schema = await ReadSchemaBodyAsync();
                var migrationStart = schema.IndexOf("/* GROUP_MIGRATION_BEGIN:", StringComparison.Ordinal);
                if (migrationStart < 0) throw new InvalidOperationException("Missing legacy schema boundary.");
                await ExecuteAsync(schema[..migrationStart] + """
                    COMMIT TRANSACTION;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        THROW;
                    END CATCH;
                    """);
            }
            else await ApplySchemaAsync();
            var factory = new EvidencijaConnectionFactory(ConnectionString);
            Accounts = new KontoRepository(new ErpConnectionFactory(ErpConnectionString));
            Records = new PpAparatRepository(factory, Accounts);
            Status = new IzvjestajEvidencijaRepository(factory, Accounts);
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task DisposeAsync()
    {
        if (!_created && !_erpCreated) return;
        EnsureDisposableName();
        await using var master = new SqlConnection(_masterConnectionString);
        if (_erpCreated)
        {
            await using (var database = new SqlConnection(ErpConnectionString)) SqlConnection.ClearPool(database);
            await master.ExecuteAsync($"ALTER DATABASE [{_erpName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_erpName}];");
            _erpCreated = false;
        }
        if (_created)
        {
            await using (var database = new SqlConnection(ConnectionString)) SqlConnection.ClearPool(database);
            await master.ExecuteAsync($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
            _created = false;
        }
    }

    private static string SchemaPath => Path.Combine(AppContext.BaseDirectory, "Sql", "Create_ppaparati.sql");
    public async Task ApplySchemaAsync() => await ExecuteAsync(await ReadSchemaBodyAsync());

    private static async Task<string> ReadSchemaBodyAsync() =>
        ExtractSchemaBody(await File.ReadAllTextAsync(SchemaPath));

    internal static string ExtractSchemaBody(string script)
    {
        const string marker = "/* SCHEMA_BEGIN:";
        var markerStart = script.IndexOf(marker, StringComparison.Ordinal);
        if (markerStart < 0)
            throw new InvalidOperationException("Missing schema boundary; refusing to execute the deployment preamble in tests.");
        var markerEnd = script.IndexOf("*/", markerStart + marker.Length, StringComparison.Ordinal);
        if (markerEnd < 0)
            throw new InvalidOperationException("Unterminated schema boundary; refusing to execute the deployment script in tests.");

        // The deployment preamble selects evidencija. Tests must remain in their disposable database.
        return script[(markerEnd + 2)..];
    }

    public async Task<string> CreateAccountAsync()
    {
        var account = "211" + Guid.NewGuid().ToString("N")[..17];
        await ExecuteErpAsync("INSERT dbo.konta (konto,naziv) VALUES (@account,N'Automated audit')", new { account });
        return account;
    }

    public async Task<int> ExecuteErpAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ErpConnectionString);
        return await connection.ExecuteAsync(sql, parameters);
    }

    public async Task<T> ScalarErpAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ErpConnectionString);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
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

    public async Task<List<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.QueryAsync<T>(sql, parameters)).AsList();
    }

    public Task<List<AuditRow>> ReadAuditAsync(string account) => QueryAsync<AuditRow>("""
        SELECT a.radnja AS Radnja, a.korisnicko_ime AS Korisnik
        FROM dbo.ppaparati_grupe_audit a INNER JOIN dbo.ppaparati_grupe g ON g.id=a.grupa_id
        WHERE g.konto=@account ORDER BY a.id
        """, new { account });

    public Task<string> SnapshotAsync() => ScalarAsync<string>("""
        SELECT (SELECT * FROM dbo.ppaparati ORDER BY id FOR XML RAW)
             + (SELECT * FROM dbo.ppaparati_grupe ORDER BY id FOR XML RAW)
             + (SELECT * FROM dbo.ppaparati_grupe_audit ORDER BY id FOR XML RAW)
             + (SELECT * FROM dbo.ppaparati_status ORDER BY id FOR XML RAW)
             + (SELECT * FROM dbo.ppaparati_status_audit ORDER BY id FOR XML RAW)
        """);

    private void EnsureDisposableName()
    {
        if (!Regex.IsMatch(_name, "^ppEvidencijaAudit_[0-9a-f]{32}$")
            || !Regex.IsMatch(_erpName, "^ppEvidencijaErpAudit_[0-9a-f]{32}$"))
            throw new InvalidOperationException("Refusing to create or remove a database outside the generated audit name.");
    }

    public sealed record AuditRow(string Radnja, string Korisnik);
}
