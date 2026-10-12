using Microsoft.Data.SqlClient;
using PpEvidencija.Models;

namespace PpEvidencija.Tests;

public sealed class GroupMigrationIntegrationTests
{
    [SqlServerFact]
    public async Task EarlierStatusTableNamesAreRenamedWithoutLosingTheirAudit()
    {
        var database = new RepositoryDatabase { LegacyOnly = true };
        await database.InitializeAsync();
        try
        {
            var account = await database.CreateAccountAsync();
            await LegacyRecordAsync(database, account, new DateTime(2026, 5, 1));
            await LegacyStatusAsync(database, account, 2026, 5, true, DateTimeOffset.UtcNow);
            var legacy = await LegacySnapshotAsync(database);
            await database.ExecuteAsync("""
                EXEC sys.sp_rename N'dbo.ppaparati_status', N'ppizvjestaji_status';
                EXEC sys.sp_rename N'dbo.ppaparati_status_audit', N'ppizvjestaji_status_audit';
                """);

            await database.ApplySchemaAsync();

            Assert.Equal(legacy, await LegacySnapshotAsync(database));
            Assert.True(Assert.Single(await database.Status.GetHistorijaAsync(account)).Zakljucen);
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task MixedLegacyMonthsMigrateWithoutLosingHistoryAndRerunPreservesMembership()
    {
        var database = new RepositoryDatabase { LegacyOnly = true };
        await database.InitializeAsync();
        try
        {
            var mixed = await database.CreateAccountAsync();
            var closedOnly = await database.CreateAccountAsync();
            var empty = await database.CreateAccountAsync();
            var when = new DateTimeOffset(2026, 2, 15, 10, 0, 0, TimeSpan.Zero);
            var firstStatus = await LegacyStatusAsync(database, mixed, 2025, 12, true, when);
            var secondStatus = await LegacyStatusAsync(database, mixed, 2026, 1, true, when);
            await LegacyStatusAsync(database, mixed, 2026, 2, false, when.AddDays(1));
            await LegacyStatusAsync(database, closedOnly, 2024, 5, true, when.AddDays(-1));
            await LegacyStatusAsync(database, empty, 2026, 1, true, when);
            foreach (var date in new[] { new DateTime(2025, 12, 31), new DateTime(2026, 1, 1),
                         new DateTime(2026, 2, 1), new DateTime(2027, 3, 15) })
                await LegacyRecordAsync(database, mixed, date);
            await LegacyRecordAsync(database, closedOnly, new DateTime(2024, 5, 10));
            var originalLegacy = await LegacySnapshotAsync(database);
            var originalRecords = await RecordSnapshotAsync(database);

            await database.ApplySchemaAsync();

            Assert.Equal(originalLegacy, await LegacySnapshotAsync(database));
            Assert.Equal(originalRecords, await RecordSnapshotAsync(database));
            Assert.Equal(5, await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ppaparati"));
            Assert.Equal(0, await database.ScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.ppaparati p LEFT JOIN dbo.ppaparati_grupe g
                  ON g.id=p.grupa_id AND g.konto=p.konto WHERE g.id IS NULL
                """));
            var groups = await database.Status.GetHistorijaAsync(mixed);
            Assert.Equal(3, groups.Count);
            var opened = Assert.Single(groups, group => !group.Zakljucen);
            Assert.Equal(opened.GrupaId, groups[0].GrupaId);
            Assert.Equal(2, opened.BrojAparata);
            Assert.Null(opened.PromijenjenoUtc);
            Assert.Equal(new[] { new DateTime(2026, 2, 1), new DateTime(2027, 3, 15) },
                (await database.Records.GetForReportAsync(opened.GrupaId, opened.Verzija)).Aparati.Select(row => row.DatumServisa));
            Assert.All(groups.Where(group => group.Zakljucen), group =>
            {
                Assert.Equal(1, group.BrojAparata);
                Assert.Equal(when, group.ZakljucenoUtc);
                Assert.Equal("Legacy closer", group.PromijenioKorisnik);
                Assert.Equal("Z", group.PosljednjaRadnjaKod);
            });
            Assert.True(groups[1].GrupaId > groups[2].GrupaId);
            Assert.Equal(new[] { firstStatus, secondStatus },
                (await database.QueryAsync<long>("""
                    SELECT izvorni_status_id FROM dbo.ppaparati_grupe
                    WHERE konto=@mixed AND zakljucen=1 ORDER BY izvorni_status_id
                    """, new { mixed })).ToArray());
            Assert.Single(await database.Status.GetHistorijaAsync(closedOnly));
            Assert.Empty(await database.Status.GetHistorijaAsync(empty));
            Assert.Equal(closedOnly, Assert.Single(await database.Status.GetPregledAsync(IzvjestajStatusFilter.Zakljuceni)).Konto);
            Assert.Equal(mixed, Assert.Single(await database.Status.GetPregledAsync(IzvjestajStatusFilter.Nezakljuceni)).Konto);
            Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ppaparati_grupe_audit"));
            var beforeRerun = await database.SnapshotAsync();
            await database.ApplySchemaAsync();
            Assert.Equal(beforeRerun, await database.SnapshotAsync());

            // A later backdated entry never moves into the migrated closed month.
            await database.Records.InsertAsync(RepositoryIntegrationTests.Item(mixed, new DateTime(2025, 12, 1)), "Current user");
            var afterInsert = await database.Status.GetHistorijaAsync(mixed);
            Assert.Equal(3, afterInsert[0].BrojAparata);
            Assert.All(afterInsert.Where(group => group.Zakljucen), group => Assert.Equal(1, group.BrojAparata));
            var afterInsertSnapshot = await database.SnapshotAsync();
            await database.ApplySchemaAsync();
            Assert.Equal(afterInsertSnapshot, await database.SnapshotAsync());
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task LatestClosedGroupUsesClosureTimeThenIdRatherThanServiceMonth()
    {
        var database = new RepositoryDatabase { LegacyOnly = true };
        await database.InitializeAsync();
        try
        {
            var account = await database.CreateAccountAsync();
            var when = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
            var latest = await LegacyStatusAsync(database, account, 2024, 1, true, when);
            await LegacyStatusAsync(database, account, 2026, 5, true, when.AddDays(-1));
            await LegacyRecordAsync(database, account, new DateTime(2024, 1, 1));
            await LegacyRecordAsync(database, account, new DateTime(2026, 5, 1));
            await database.ApplySchemaAsync();
            var groups = await database.Status.GetHistorijaAsync(account);
            var overview = Assert.Single(await database.Status.GetPregledAsync(IzvjestajStatusFilter.Svi));
            Assert.Equal(groups[0].GrupaId, overview.GrupaId);
            Assert.Equal(latest, await database.ScalarAsync<long>(
                "SELECT izvorni_status_id FROM dbo.ppaparati_grupe WHERE id=@id", new { id = overview.GrupaId }));

            await database.ExecuteAsync("""
                UPDATE dbo.ppaparati_grupe SET zakljuceno_utc=@when, promijenjeno_utc=@when
                WHERE konto=@account
                """, new { when, account });
            var tied = Assert.Single(await database.Status.GetPregledAsync(IzvjestajStatusFilter.Svi));
            Assert.Equal(groups.Max(group => group.GrupaId), tied.GrupaId);
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task MigrationFailureRollsBackSchemaAndMembershipAndCanBeRetried()
    {
        var database = new RepositoryDatabase { LegacyOnly = true };
        await database.InitializeAsync();
        try
        {
            var account = await database.CreateAccountAsync();
            await LegacyRecordAsync(database, account, new DateTime(2026, 5, 1));
            await database.ExecuteAsync("""
                CREATE TRIGGER dbo.RejectTestMigration ON dbo.ppaparati AFTER UPDATE
                AS BEGIN IF EXISTS (SELECT 1 FROM inserted) THROW 51003, 'Injected migration failure', 1; END;
                """);
            var before = await RecordSnapshotAsync(database);

            var error = await Assert.ThrowsAsync<SqlException>(() => database.ApplySchemaAsync());

            Assert.Equal(51003, error.Number);
            Assert.Equal(before, await RecordSnapshotAsync(database));
            Assert.Equal(0, await database.ScalarAsync<int>("""
                SELECT COUNT(*) FROM sys.tables WHERE name IN ('ppaparati_grupe','ppaparati_grupe_audit')
                """));
            Assert.Equal(0, await database.ScalarAsync<int>("""
                SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ppaparati') AND name='grupa_id'
                """));
            await database.ExecuteAsync("DROP TRIGGER dbo.RejectTestMigration");
            await database.ApplySchemaAsync();
            Assert.Equal(1, Assert.Single(await database.Status.GetHistorijaAsync(account)).BrojAparata);
        }
        finally { await database.DisposeAsync(); }
    }

    private static Task LegacyRecordAsync(RepositoryDatabase database, string account, DateTime date) =>
        database.ExecuteAsync("""
            INSERT dbo.ppaparati (konto,tip,punjenje_kg,serijski_broj_aparata,godina_proizvodnje,
                datum_servisa,konstatacija_ispravnosti,vozilo,ispitivanje_izvrsio)
            VALUES (@account,N'S',6,@serial,2020,@date,N'Ispravan',N'N/A',N'Legacy test')
            """, new { account, date, serial = Guid.NewGuid().ToString("N") });

    private static Task<long> LegacyStatusAsync(RepositoryDatabase database, string account, int year, int month,
        bool closed, DateTimeOffset when) => database.ScalarAsync<long>("""
        INSERT dbo.ppaparati_status (konto,godina,mjesec,zakljucen,posljednja_radnja,promijenio_korisnik,promijenjeno_utc)
        VALUES (@account,@year,@month,@closed,CASE WHEN @closed=1 THEN 'Z' ELSE 'O' END,
            CASE WHEN @closed=1 THEN N'Legacy closer' ELSE N'Legacy opener' END,@when);
        DECLARE @id bigint = SCOPE_IDENTITY();
        INSERT dbo.ppaparati_status_audit (status_id,radnja,korisnicko_ime,dogadjaj_utc)
        VALUES (@id,'O',N'Original author',DATEADD(day,-1,@when));
        IF @closed=1 INSERT dbo.ppaparati_status_audit (status_id,radnja,korisnicko_ime,dogadjaj_utc)
            VALUES (@id,'Z',N'Legacy closer',@when);
        SELECT @id;
        """, new { account, year, month, closed, when });

    private static Task<string> LegacySnapshotAsync(RepositoryDatabase database) => database.ScalarAsync<string>("""
        SELECT (SELECT * FROM dbo.ppaparati_status ORDER BY id FOR XML RAW)
             + (SELECT * FROM dbo.ppaparati_status_audit ORDER BY id FOR XML RAW)
        """);

    private static Task<string> RecordSnapshotAsync(RepositoryDatabase database) => database.ScalarAsync<string>("""
        SELECT (SELECT id,konto,tip,punjenje_kg,serijski_broj_aparata,godina_proizvodnje,datum_servisa,
            sljedeci_servis,konstatacija_ispravnosti,vozilo,ispitivanje_izvrsio
            FROM dbo.ppaparati ORDER BY id FOR XML RAW)
        """);
}
