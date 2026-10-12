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
        var years = Control<ComboBox>("cmbGodina");
        years.ItemsSource = new[] { 2026 };
        years.SelectedItem = 2026;
        Control<DatePicker>("dpDatumZakljucivanja").SelectedDate = new DateTime(2026, 9, 30);
    }

    public T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);

    public void Select(IzvjestajPregledRed row)
    {
        var grid = Control<DataGrid>("dgKontaIzvjestaji");
        grid.ItemsSource = new[] { row };
        grid.SelectedItem = row;
    }

    public static IzvjestajPregledRed Row(string konto = "A", int month = 8, bool closed = false) => new()
    {
        Konto = konto, NazivKupca = "Kupac " + konto, Mjesec = month, Godina = 2026, BrojAparata = 1, Zakljucen = closed
    };

    public static IReadOnlyList<PpAparatRecord> Records(string konto, int month = 8) => [new()
    {
        Konto = konto, Id = 1, Tip = "S 6", PunjenjeKg = 6, SerijskiBroj = konto + "-001",
        GodinaProizvodnje = 2020, DatumServisa = new DateTime(2026, month, 9),
        SljedeciServis = new DateTime(2026, month, 9).AddMonths(6),
        KonstatacijaIspravnosti = "Ispravan", Vozilo = "N/A", IspitivanjeIzvrsio = "Test"
    }];

    public void Dispose() => Window.Close();
}

internal sealed class FakeAparatRepository : IPpAparatRepository
{
    public Func<string, int, int, CancellationToken, Task<IReadOnlyList<PpAparatRecord>>> Read { get; set; } =
        (konto, month, _, _) => Task.FromResult(WindowTestContext.Records(konto, month));
    public Task<IReadOnlyList<PpAparatRecord>> GetForReportAsync(string konto, int month, int year, CancellationToken cancellationToken = default) => Read(konto, month, year, cancellationToken);
    public Task<long> InsertAsync(PpAparatInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([2026]);
}

internal sealed record StatusChange(string Konto, int Month, int Year, bool Expected, bool Closed, string User);

internal sealed class FakeStatusRepository : IIzvjestajEvidencijaRepository
{
    public List<StatusChange> Changes { get; } = [];
    public Func<Task<IReadOnlyList<IzvjestajPregledRed>>> Overview { get; set; } = () => Task.FromResult<IReadOnlyList<IzvjestajPregledRed>>([]);
    public Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(int month, int year, IzvjestajStatusFilter statusFilter, CancellationToken cancellationToken = default) => Overview();
    public Task PromijeniStatusAsync(string konto, int month, int year, bool ocekivanoZakljucen, bool zakljucen, string korisnickoIme, CancellationToken cancellationToken = default)
    {
        Changes.Add(new(konto, month, year, ocekivanoZakljucen, zakljucen, korisnickoIme));
        return Task.CompletedTask;
    }
}

internal sealed class FakeDocxService : IDocxReportService
{
    public List<IzvjestajZahtjev> Requests { get; } = [];
    public Task GenerateAsync(IzvjestajZahtjev request, string outputPath, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }
}

internal sealed class FakeReportDialogs : IReportDialogs
{
    public Func<IzvjestajPregledRed, bool, bool> Confirm { get; set; } = (_, _) => true;
    public Func<string?> Choose { get; set; } = () => Path.Combine(Path.GetTempPath(), "ppEvidencija-ui-test.docx");
    public bool ConfirmStatusChange(Window owner, IzvjestajPregledRed selected, bool close) => Confirm(selected, close);
    public string? ChooseOutputPath(Window owner, string fileName) => Choose();
}
