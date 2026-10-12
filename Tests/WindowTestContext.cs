using System.Windows;
using System.Windows.Controls;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;
using PpEvidencija.Views;

namespace PpEvidencija.Tests;

internal sealed class WindowTestContext : IDisposable
{
    public FakeAparatRepository Aparati { get; } = new();
    public FakeStatusRepository Reports { get; } = new();
    public FakeDocxService Docx { get; } = new();
    public FakeReportDialogs Dialogs { get; } = new();
    public MainWindow Window { get; }

    public WindowTestContext()
    {
        Window = new MainWindow(new("tester", "tester", "Test", "User"),
            new KontoRepository(new SqlConnectionFactory("Server=invalid;Integrated Security=true")),
            Aparati, Reports, new PpAparatValidator(), Docx, Dialogs);
        Control<DatePicker>("dpDatumZakljucivanja").SelectedDate = new DateTime(2026, 9, 30);
    }

    public T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);

    public void Register(IzvjestajPregledRed row)
    {
        Aparati.Snapshots[row.GrupaId] = Snapshot(row);
        Reports.Groups[row.Konto] = [row];
    }

    public void Select(IzvjestajPregledRed row)
    {
        Register(row);
        var grid = Control<DataGrid>("dgKontaIzvjestaji");
        grid.ItemsSource = new[] { row };
        grid.SelectedItem = row;
    }

    public static IzvjestajPregledRed Row(string konto = "A", long grupaId = 0, bool closed = false, long version = 1) => new()
    {
        Konto = konto, NazivKupca = "Kupac " + konto, GrupaId = grupaId == 0 ? konto[0] - 'A' + 1 : grupaId,
        Verzija = version, BrojAparata = 1, Zakljucen = closed,
        DatumOd = new DateTime(2026, 8, 9), DatumDo = new DateTime(2026, 8, 9),
        ZakljucenoUtc = closed ? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) : null
    };

    public static IzvjestajSnimak Snapshot(IzvjestajPregledRed row) => new(row, Records(row.Konto, row.GrupaId));

    public static IReadOnlyList<PpAparatRecord> Records(string konto, long grupaId = 1) => [new()
    {
        Konto = konto, GrupaId = grupaId, Id = grupaId * 100 + 1, Tip = "S 6", PunjenjeKg = 6,
        SerijskiBroj = $"{konto}-{grupaId}-001", GodinaProizvodnje = 2020,
        DatumServisa = new DateTime(2026, 8, 9), SljedeciServis = new DateTime(2027, 2, 9),
        KonstatacijaIspravnosti = "Ispravan", Vozilo = "N/A", IspitivanjeIzvrsio = "Test"
    }];

    public void Dispose() => Window.Close();
}

internal sealed class FakeAparatRepository : IPpAparatRepository
{
    public Dictionary<long, IzvjestajSnimak> Snapshots { get; } = [];
    public Func<long, long, CancellationToken, Task<IzvjestajSnimak>>? Read { get; set; }
    public List<(PpAparatInput Input, string User)> Inserts { get; } = [];
    public Func<Task<long>> Insert { get; set; } = () => Task.FromResult(100L);
    public Task<IzvjestajSnimak> GetForReportAsync(long grupaId, long ocekivanaVerzija, CancellationToken cancellationToken = default) =>
        Read?.Invoke(grupaId, ocekivanaVerzija, cancellationToken) ?? Task.FromResult(Snapshots[grupaId]);
    public Task<long> InsertAsync(PpAparatInput input, string korisnickoIme, CancellationToken cancellationToken = default)
    {
        Inserts.Add((input, korisnickoIme));
        return Insert();
    }
}

internal sealed record StatusChange(long GrupaId, long Version, string User);

internal sealed class FakeStatusRepository : IIzvjestajEvidencijaRepository
{
    public Dictionary<string, IReadOnlyList<IzvjestajPregledRed>> Groups { get; } = [];
    public List<StatusChange> Changes { get; } = [];
    public List<IzvjestajStatusFilter> Filters { get; } = [];
    public Func<Task<IReadOnlyList<IzvjestajPregledRed>>> Overview { get; set; } = () => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([]);
    public Func<string, CancellationToken, Task<IReadOnlyList<IzvjestajPregledRed>>>? History { get; set; }
    public Func<Task> Close { get; set; } = () => Task.CompletedTask;
    public Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(IzvjestajStatusFilter statusFilter, CancellationToken cancellationToken = default)
    {
        Filters.Add(statusFilter);
        return Overview();
    }
    public Task<IReadOnlyList<IzvjestajPregledRed>> GetHistorijaAsync(string konto, CancellationToken cancellationToken = default) =>
        History?.Invoke(konto, cancellationToken) ?? Task.FromResult(Groups[konto]);
    public Task ZakljuciAsync(long grupaId, long ocekivanaVerzija, string korisnickoIme, CancellationToken cancellationToken = default)
    {
        Changes.Add(new(grupaId, ocekivanaVerzija, korisnickoIme));
        return Close();
    }
}

internal sealed class FakeDocxService : IDocxReportService
{
    public List<IzvjestajZahtjev> Requests { get; } = [];
    public Func<Task> Generate { get; set; } = () => Task.CompletedTask;
    public Task GenerateAsync(IzvjestajZahtjev request, string outputPath, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Generate();
    }
}

internal sealed class FakeReportDialogs : IReportDialogs
{
    public Func<IzvjestajPregledRed, bool> Confirm { get; set; } = _ => true;
    public Func<string?> Choose { get; set; } = () => Path.Combine(Path.GetTempPath(), "ppEvidencija-ui-test.docx");
    public List<string> Errors { get; } = [];
    public List<string> FileNames { get; } = [];
    public bool ConfirmClose(Window owner, IzvjestajPregledRed selected) => Confirm(selected);
    public string? ChooseOutputPath(Window owner, string fileName)
    {
        FileNames.Add(fileName);
        return Choose();
    }
    public void ShowError(Window owner, string message, string title) => Errors.Add(message);
}