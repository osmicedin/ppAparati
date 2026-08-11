using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;

namespace PpEvidencija.Views;

public partial class MainWindow : Window
{
    private const string DefaultKontoPrefix = "211";

    private readonly AutentifikovaniKorisnik _korisnik;
    private readonly KontoRepository _kontoRepository;
    private readonly PpAparatRepository _ppAparatRepository;
    private readonly PpAparatValidator _validator;
    private readonly IDocxReportService _docxReportService;
    private IReadOnlyList<PpAparatRecord> _trenutniIzvjestaj = [];
    private string? _zadnjiDocxPath;
    private CancellationTokenSource? _kontoUnosSearchCancellation;
    private CancellationTokenSource? _kontoIzvjestajSearchCancellation;
    private bool _suppressKontoSearch;

    public MainWindow(
        AutentifikovaniKorisnik korisnik,
        KontoRepository kontoRepository,
        PpAparatRepository ppAparatRepository,
        PpAparatValidator validator,
        IDocxReportService docxReportService)
    {
        InitializeComponent();
        _korisnik = korisnik;
        _kontoRepository = kontoRepository;
        _ppAparatRepository = ppAparatRepository;
        _validator = validator;
        _docxReportService = docxReportService;

        txtPrijavljeniKorisnik.Text = $"Korisnik: {_korisnik.KorisnickoIme}";
        dpDatumServisa.SelectedDate = DateTime.Today;
        dpDatumZakljucivanja.SelectedDate = DateTime.Today;
        txtGodinaProizvodnje.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        txtKonstatacija.Text = "Ispravan";
        txtIspitivanjeIzvrsio.Text = _korisnik.PunoIme;
        cmbTip.ItemsSource = new[] { "S6", "S3" };

        cmbKontoUnos.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(KontoComboBox_TextChanged));
        cmbKontoIzvjestaj.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(KontoComboBox_TextChanged));

        cmbMjesec.ItemsSource = KreirajMjesece();
        cmbMjesec.SelectedIndex = DateTime.Today.Month - 1;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UcitajPocetnePodatkeAsync();
    }

    private async Task UcitajPocetnePodatkeAsync()
    {
        IsEnabled = false;
        txtGlobalStatus.Text = "Učitavanje dostupnih godina...";

        try
        {
            var years = (await _ppAparatRepository.GetAvailableYearsAsync()).ToList();
            if (!years.Contains(DateTime.Today.Year))
            {
                years.Add(DateTime.Today.Year);
            }

            years.Sort((left, right) => right.CompareTo(left));
            cmbGodina.ItemsSource = years;
            cmbGodina.SelectedItem = DateTime.Today.Year;

            txtGlobalStatus.Text = "Konto tražite po početku broja ili dijelu naziva kupca.";
        }
        catch (Exception ex)
        {
            txtGlobalStatus.Text = "Početni podaci nisu učitani.";
            MessageBox.Show(
                $"Nije moguće učitati početne podatke.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Greška baze",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void BtnSnimi_Click(object sender, RoutedEventArgs e)
    {
        txtUnosStatus.Text = string.Empty;
        txtUnosStatus.Foreground = System.Windows.Media.Brushes.Firebrick;

        if (!TryBuildInput(out var input, out var parseErrors))
        {
            txtUnosStatus.Text = string.Join(Environment.NewLine, parseErrors);
            return;
        }

        var errors = _validator.Validate(input!);
        if (errors.Count > 0)
        {
            txtUnosStatus.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        btnSnimi.IsEnabled = false;
        txtGlobalStatus.Text = "Snimanje PP aparata...";

        try
        {
            var id = await _ppAparatRepository.InsertAsync(input!);
            txtUnosStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            txtUnosStatus.Text = $"PP aparat je uspješno snimljen. ID zapisa: {id}.";
            txtGlobalStatus.Text = "Snimanje završeno.";
            OcistiPoljaUnosa(zadrziKonto: true);
            await OsvjeziGodineAsync(input!.DatumServisa.Year);
        }
        catch (Exception ex)
        {
            txtUnosStatus.Text = $"Snimanje nije uspjelo: {ex.Message}";
            txtGlobalStatus.Text = "Greška pri snimanju.";
        }
        finally
        {
            btnSnimi.IsEnabled = true;
        }
    }

    private void BtnOcisti_Click(object sender, RoutedEventArgs e)
    {
        OcistiPoljaUnosa(zadrziKonto: true);
        txtUnosStatus.Text = string.Empty;
    }

    private void OcistiPoljaUnosa(bool zadrziKonto)
    {
        if (!zadrziKonto)
        {
            cmbKontoUnos.SelectedItem = null;
            cmbKontoUnos.Text = string.Empty;
        }

        cmbTip.SelectedItem = null;
        txtPunjenje.Clear();
        txtSerijskiBroj.Clear();
        txtGodinaProizvodnje.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        dpDatumServisa.SelectedDate = DateTime.Today;
        txtKonstatacija.Text = "Ispravan";
        txtVozilo.Clear();
        txtIspitivanjeIzvrsio.Text = _korisnik.PunoIme;
        cmbTip.Focus();
    }

    private async void KontoComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressKontoSearch || sender is not ComboBox comboBox || !comboBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (comboBox.SelectedItem is Konto selected
            && string.Equals(comboBox.Text.Trim(), selected.Prikaz, StringComparison.Ordinal))
        {
            return;
        }

        await SearchKontaAsync(comboBox, useDebounce: true);
    }

    private async void KontoComboBox_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.Items.Count > 0)
        {
            return;
        }

        var defaultSearch = string.IsNullOrWhiteSpace(comboBox.Text)
            ? DefaultKontoPrefix
            : null;
        await SearchKontaAsync(comboBox, useDebounce: false, searchOverride: defaultSearch);
    }

    private async Task SearchKontaAsync(
        ComboBox comboBox,
        bool useDebounce,
        string? searchOverride = null)
    {
        var cancellation = new CancellationTokenSource();
        if (ReferenceEquals(comboBox, cmbKontoUnos))
        {
            _kontoUnosSearchCancellation?.Cancel();
            _kontoUnosSearchCancellation?.Dispose();
            _kontoUnosSearchCancellation = cancellation;
        }
        else
        {
            _kontoIzvjestajSearchCancellation?.Cancel();
            _kontoIzvjestajSearchCancellation?.Dispose();
            _kontoIzvjestajSearchCancellation = cancellation;
        }

        var token = cancellation.Token;
        var enteredText = comboBox.Text;
        var searchText = (searchOverride ?? enteredText).Trim();

        try
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                _suppressKontoSearch = true;
                try
                {
                    comboBox.ItemsSource = Array.Empty<Konto>();
                    comboBox.SelectedItem = null;
                    comboBox.Text = string.Empty;
                    comboBox.IsDropDownOpen = false;
                }
                finally
                {
                    _suppressKontoSearch = false;
                }

                txtGlobalStatus.Text = "Upišite početak broja konta ili dio naziva kupca.";
                return;
            }

            if (useDebounce)
            {
                await Task.Delay(250, token);
            }

            var konta = await _kontoRepository.SearchAsync(searchText, cancellationToken: token);
            token.ThrowIfCancellationRequested();

            if (searchOverride is null
                && !string.Equals(searchText, comboBox.Text.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            if (searchOverride is not null && !string.IsNullOrWhiteSpace(comboBox.Text))
            {
                return;
            }

            _suppressKontoSearch = true;
            try
            {
                comboBox.ItemsSource = konta;
                comboBox.SelectedItem = null;
                comboBox.Text = enteredText;
                if (comboBox.IsKeyboardFocusWithin)
                {
                    comboBox.IsDropDownOpen = true;
                }
            }
            finally
            {
                _suppressKontoSearch = false;
            }

            txtGlobalStatus.Text = konta.Count == 0
                ? $"Nema konta ni naziva za pretragu '{searchText}'."
                : searchOverride is not null
                    ? $"Prikazana početna konta {DefaultKontoPrefix}%."
                    : $"Pronađeno konta: {konta.Count}.";
        }
        catch (OperationCanceledException)
        {
            // Nova pretraga je zamijenila prethodnu.
        }
        catch (Exception ex)
        {
            txtGlobalStatus.Text = $"Pretraga konta nije uspjela: {ex.Message}";
        }
    }

    private bool TryBuildInput(out PpAparatInput? input, out IReadOnlyList<string> errors)
    {
        input = null;
        var validationErrors = new List<string>();
        var konto = cmbKontoUnos.SelectedItem as Konto;
        var punjenje = 0m;
        short godina = 0;
        var datumServisa = default(DateTime);

        if (konto is null
            || !string.Equals(cmbKontoUnos.Text.Trim(), konto.Prikaz, StringComparison.Ordinal))
        {
            validationErrors.Add("Odaberite konto iz liste.");
        }

        if (!TryParseDecimal(txtPunjenje.Text, out punjenje))
        {
            validationErrors.Add("Punjenje mora biti ispravan decimalni broj.");
        }

        if (!short.TryParse(txtGodinaProizvodnje.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out godina))
        {
            validationErrors.Add("Godina proizvodnje mora biti cijeli broj.");
        }

        if (dpDatumServisa.SelectedDate is DateTime selectedDate)
        {
            datumServisa = selectedDate;
        }
        else
        {
            validationErrors.Add("Odaberite datum servisa.");
        }

        errors = validationErrors;
        if (validationErrors.Count > 0)
        {
            return false;
        }

        input = new PpAparatInput(
            konto!.Sifra.Trim(),
            cmbTip.SelectedItem as string ?? string.Empty,
            punjenje,
            txtSerijskiBroj.Text.Trim(),
            godina,
            datumServisa.Date,
            txtKonstatacija.Text.Trim(),
            txtVozilo.Text.Trim(),
            txtIspitivanjeIzvrsio.Text.Trim());
        return true;
    }

    private async void BtnUcitajPregled_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReportFilter(out var konto, out var month, out var year, out var error))
        {
            MessageBox.Show(error, "Provjera podataka", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        btnUcitajPregled.IsEnabled = false;
        try
        {
            await UcitajIzvjestajAsync(konto!, month, year);
        }
        finally
        {
            btnUcitajPregled.IsEnabled = true;
        }
    }

    private async void BtnGenerisiDocx_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReportFilter(out var konto, out var month, out var year, out var filterError))
        {
            MessageBox.Show(filterError, "Provjera podataka", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var broj = txtBrojZapisnika.Text.Trim();
        if (string.IsNullOrWhiteSpace(broj))
        {
            MessageBox.Show("Unesite broj zapisnika.", "Provjera podataka", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtBrojZapisnika.Focus();
            return;
        }

        if (dpDatumZakljucivanja.SelectedDate is not DateTime datumZakljucivanja)
        {
            MessageBox.Show("Odaberite datum zaključivanja.", "Provjera podataka", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        btnGenerisiDocx.IsEnabled = false;
        txtIzvjestajStatus.Text = "Priprema izvještaja...";

        try
        {
            await UcitajIzvjestajAsync(konto!, month, year);
            if (_trenutniIzvjestaj.Count == 0)
            {
                MessageBox.Show(
                    "Za odabrani konto, mjesec i godinu nema podataka.",
                    "Nema podataka",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "Sačuvaj zapisnik",
                Filter = "Word dokument (*.docx)|*.docx",
                AddExtension = true,
                DefaultExt = ".docx",
                FileName = $"Zapisnik_{SafeFileName(konto!.Sifra)}_{year}-{month:00}.docx"
            };

            if (saveDialog.ShowDialog(this) != true)
            {
                txtIzvjestajStatus.Text = "Generisanje je otkazano.";
                return;
            }

            var request = new IzvjestajZahtjev(
                broj,
                datumZakljucivanja.Date,
                konto!,
                month,
                year,
                _trenutniIzvjestaj);

            await _docxReportService.GenerateAsync(request, saveDialog.FileName);
            _zadnjiDocxPath = saveDialog.FileName;
            btnOtvoriWord.IsEnabled = true;
            txtIzvjestajStatus.Text = $"Dokument je sačuvan: {Path.GetFileName(saveDialog.FileName)}";
            txtGlobalStatus.Text = "DOCX izvještaj je generisan.";
        }
        catch (Exception ex)
        {
            txtIzvjestajStatus.Text = "Generisanje nije uspjelo.";
            MessageBox.Show(
                $"Nije moguće generisati DOCX.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Greška izvještaja",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            btnGenerisiDocx.IsEnabled = true;
        }
    }

    private void BtnOtvoriWord_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_zadnjiDocxPath) || !File.Exists(_zadnjiDocxPath))
        {
            MessageBox.Show("Generisani dokument više nije dostupan.", "Dokument", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _zadnjiDocxPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Dokument je sačuvan, ali ga nije moguće otvoriti. Provjerite da li je Word instaliran.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Otvaranje dokumenta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task UcitajIzvjestajAsync(Konto konto, int month, int year)
    {
        txtIzvjestajStatus.Text = "Učitavanje podataka...";
        _trenutniIzvjestaj = await _ppAparatRepository.GetForReportAsync(konto.Sifra, month, year);
        dgIzvjestaj.ItemsSource = _trenutniIzvjestaj
            .Select((item, index) => new IzvjestajRed(index + 1, item))
            .ToList();
        txtIzvjestajStatus.Text = _trenutniIzvjestaj.Count == 0
            ? "Nema podataka za odabrani period."
            : $"Pronađeno zapisa: {_trenutniIzvjestaj.Count}.";
    }

    private bool TryGetReportFilter(
        out Konto? konto,
        out int month,
        out int year,
        out string error)
    {
        konto = cmbKontoIzvjestaj.SelectedItem as Konto;
        month = (cmbMjesec.SelectedItem as MjesecStavka)?.Broj ?? 0;
        error = string.Empty;

        var yearText = cmbGodina.Text.Trim();
        if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
            || year is < 1900 or > 9999)
        {
            error = "Odaberite ispravnu godinu.";
            return false;
        }

        if (konto is null
            || !string.Equals(cmbKontoIzvjestaj.Text.Trim(), konto.Prikaz, StringComparison.Ordinal))
        {
            error = "Odaberite konto iz liste.";
            return false;
        }

        if (month is < 1 or > 12)
        {
            error = "Odaberite mjesec.";
            return false;
        }

        return true;
    }

    private async Task OsvjeziGodineAsync(int newYear)
    {
        var years = (cmbGodina.ItemsSource as IEnumerable<int>)?.ToList() ?? [];
        if (!years.Contains(newYear))
        {
            years.Add(newYear);
            years.Sort((left, right) => right.CompareTo(left));
            cmbGodina.ItemsSource = years;
        }

        await Task.CompletedTask;
    }

    private void DpDatumServisa_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        txtSljedeciServis.Text = dpDatumServisa.SelectedDate is DateTime date
            ? date.AddMonths(6).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static bool TryParseDecimal(string value, out decimal number)
    {
        var styles = NumberStyles.Number;
        return decimal.TryParse(value.Trim(), styles, CultureInfo.CurrentCulture, out number)
            || decimal.TryParse(value.Trim(), styles, CultureInfo.GetCultureInfo("bs-Latn-BA"), out number)
            || decimal.TryParse(value.Trim().Replace(',', '.'), styles, CultureInfo.InvariantCulture, out number);
    }

    private static IReadOnlyList<MjesecStavka> KreirajMjesece() =>
    [
        new(1, "Januar"),
        new(2, "Februar"),
        new(3, "Mart"),
        new(4, "April"),
        new(5, "Maj"),
        new(6, "Juni"),
        new(7, "Juli"),
        new(8, "August"),
        new(9, "Septembar"),
        new(10, "Oktobar"),
        new(11, "Novembar"),
        new(12, "Decembar")
    ];

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
