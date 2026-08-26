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
    private readonly IzvjestajEvidencijaRepository _izvjestajEvidencijaRepository;
    private readonly PpAparatValidator _validator;
    private readonly IDocxReportService _docxReportService;
    private IReadOnlyList<PpAparatRecord> _trenutniIzvjestaj = [];
    private IzvjestajPregledRed? _odabraniPregled;
    private string? _zadnjiDocxPath;
    private CancellationTokenSource? _kontoUnosSearchCancellation;
    private CancellationTokenSource? _pregledKontaCancellation;
    private CancellationTokenSource? _detaljiIzvjestajaCancellation;
    private bool _suppressKontoSearch;
    private bool _reportFiltersReady;
    private int _pregledMjesec;
    private int _pregledGodina;

    public MainWindow(
        AutentifikovaniKorisnik korisnik,
        KontoRepository kontoRepository,
        PpAparatRepository ppAparatRepository,
        IzvjestajEvidencijaRepository izvjestajEvidencijaRepository,
        PpAparatValidator validator,
        IDocxReportService docxReportService)
    {
        InitializeComponent();
        _korisnik = korisnik;
        _kontoRepository = kontoRepository;
        _ppAparatRepository = ppAparatRepository;
        _izvjestajEvidencijaRepository = izvjestajEvidencijaRepository;
        _validator = validator;
        _docxReportService = docxReportService;

        txtPrijavljeniKorisnik.Text = $"Korisnik: {_korisnik.KorisnickoIme}";
        dpDatumServisa.SelectedDate = DateTime.Today;
        dpDatumZakljucivanja.SelectedDate = DateTime.Today;
        txtGodinaProizvodnje.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        txtKonstatacija.Text = "Ispravan";
        txtIspitivanjeIzvrsio.Text = _korisnik.PunoIme;
        cmbTip.ItemsSource = new[] { "S 1", "S 2", "S 3", "S 6", "S 9", "Co2", "S 50" };

        cmbKontoUnos.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(KontoComboBox_TextChanged));

        cmbMjesec.ItemsSource = KreirajMjesece();
        cmbMjesec.SelectedIndex = DateTime.Today.Month - 1;
        cmbStatusIzvjestaja.ItemsSource = KreirajStatusFiltere();
        cmbStatusIzvjestaja.SelectedIndex = 0;

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

            _reportFiltersReady = true;
            await UcitajPregledKontaAsync();
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
        _kontoUnosSearchCancellation?.Cancel();
        _kontoUnosSearchCancellation?.Dispose();
        _kontoUnosSearchCancellation = cancellation;

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

    private async void ReportFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reportFiltersReady)
        {
            await UcitajPregledKontaAsync();
        }
    }

    private async void BtnOsvjeziPregledKonta_Click(object sender, RoutedEventArgs e)
    {
        await UcitajPregledKontaAsync(_odabraniPregled?.Konto);
    }

    private async Task UcitajPregledKontaAsync(string? zadrziKonto = null)
    {
        if (!TryGetOverviewFilter(out var month, out var year, out var statusFilter, out var error))
        {
            txtPregledKontaStatus.Text = error;
            ClearSelectedReport();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _pregledKontaCancellation?.Cancel();
        _pregledKontaCancellation?.Dispose();
        _pregledKontaCancellation = cancellation;
        var token = cancellation.Token;

        btnOsvjeziPregledKonta.IsEnabled = false;
        txtPregledKontaStatus.Text = "Učitavanje kupaca...";
        pnlPrazanPregled.Visibility = Visibility.Visible;
        txtPrazanPregled.Text = "Učitavanje kupaca...";

        try
        {
            var rows = await _izvjestajEvidencijaRepository.GetPregledAsync(
                month,
                year,
                statusFilter,
                token);
            token.ThrowIfCancellationRequested();

            _pregledMjesec = month;
            _pregledGodina = year;
            dgKontaIzvjestaji.ItemsSource = rows;

            var rowToKeep = string.IsNullOrWhiteSpace(zadrziKonto)
                ? null
                : rows.FirstOrDefault(row => string.Equals(
                    row.Konto,
                    zadrziKonto,
                    StringComparison.Ordinal));

            if (rowToKeep is null)
            {
                dgKontaIzvjestaji.SelectedItem = null;
                ClearSelectedReport();
            }
            else
            {
                dgKontaIzvjestaji.SelectedItem = rowToKeep;
                dgKontaIzvjestaji.ScrollIntoView(rowToKeep);
            }

            txtPregledKontaStatus.Text = rows.Count == 0
                ? "Nema kupaca za odabrani period i status."
                : $"Prikazano kupaca: {rows.Count}.";
            pnlPrazanPregled.Visibility = rows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            txtPrazanPregled.Text = "Nema kupaca za odabrani period i status.";
            txtGlobalStatus.Text = $"Učitan je pregled za {month:00}/{year}.";
        }
        catch (OperationCanceledException)
        {
            // Nova promjena filtera je zamijenila prethodno učitavanje.
        }
        catch (Exception ex)
        {
            dgKontaIzvjestaji.ItemsSource = null;
            ClearSelectedReport();
            txtPregledKontaStatus.Text = "Pregled kupaca nije učitan.";
            pnlPrazanPregled.Visibility = Visibility.Visible;
            txtPrazanPregled.Text = "Pregled trenutno nije dostupan.";
            txtGlobalStatus.Text = "Greška pri učitavanju mjesečne evidencije.";
            MessageBox.Show(
                $"Nije moguće učitati mjesečnu evidenciju.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Greška baze",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_pregledKontaCancellation, cancellation))
            {
                btnOsvjeziPregledKonta.IsEnabled = true;
            }
        }
    }

    private async void DgKontaIzvjestaji_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dgKontaIzvjestaji.SelectedItem is not IzvjestajPregledRed selected)
        {
            ClearSelectedReport();
            return;
        }

        _odabraniPregled = selected;
        pnlBezOdabranogKupca.Visibility = Visibility.Collapsed;
        pnlOdabraniKupac.Visibility = Visibility.Visible;
        pnlOdabraniKupac.DataContext = selected;
        txtOdabraniKupac.Text = selected.Kupac.Prikaz;
        var monthName = (cmbMjesec.SelectedItem as MjesecStavka)?.Naziv ?? $"{_pregledMjesec:00}. mjesec";
        txtOdabraniPeriod.Text = $"{monthName} {_pregledGodina} • {FormatirajBrojAparata(selected.BrojAparata)}";
        btnPromijeniStatus.Content = selected.Zakljucen
            ? "Ponovo otvori"
            : "Zaključi";
        btnPromijeniStatus.ToolTip = selected.Zakljucen
            ? "Dozvoli nove unose za ovaj konto i period."
            : "Označi izvještaj za ovaj konto i period kao završen.";
        btnPromijeniStatus.IsEnabled = true;
        btnGenerisiDocx.IsEnabled = true;

        var cancellation = new CancellationTokenSource();
        _detaljiIzvjestajaCancellation?.Cancel();
        _detaljiIzvjestajaCancellation?.Dispose();
        _detaljiIzvjestajaCancellation = cancellation;

        try
        {
            await UcitajIzvjestajAsync(
                selected.Kupac,
                _pregledMjesec,
                _pregledGodina,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Izabran je drugi kupac prije završetka učitavanja.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_odabraniPregled, selected))
            {
                _trenutniIzvjestaj = [];
                dgIzvjestaj.ItemsSource = null;
                txtIzvjestajStatus.Text = "Podaci kupca nisu učitani.";
                MessageBox.Show(
                    $"Nije moguće učitati aparate odabranog kupca.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "Greška baze",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private async void BtnPromijeniStatus_Click(object sender, RoutedEventArgs e)
    {
        var selected = _odabraniPregled;
        if (selected is null)
        {
            MessageBox.Show(
                "Odaberite kupca iz tabele.",
                "Mjesečna evidencija",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var noviStatus = !selected.Zakljucen;
        var actionText = noviStatus ? "zaključiti" : "ponovo otvoriti";
        var confirmation = MessageBox.Show(
            $"Da li želite {actionText} period {_pregledMjesec:00}/{_pregledGodina} za kupca:{Environment.NewLine}{selected.Kupac.Prikaz}?",
            noviStatus ? "Zaključivanje perioda" : "Ponovno otvaranje perioda",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        btnPromijeniStatus.IsEnabled = false;
        btnGenerisiDocx.IsEnabled = false;
        txtGlobalStatus.Text = noviStatus ? "Zaključivanje perioda..." : "Ponovno otvaranje perioda...";

        try
        {
            await _izvjestajEvidencijaRepository.PromijeniStatusAsync(
                selected.Konto,
                _pregledMjesec,
                _pregledGodina,
                selected.Zakljucen,
                noviStatus,
                _korisnik.KorisnickoIme);

            await UcitajPregledKontaAsync(selected.Konto);
            txtGlobalStatus.Text = noviStatus
                ? "Period je uspješno zaključen."
                : "Period je uspješno ponovo otvoren.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Status perioda nije promijenjen.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Mjesečna evidencija",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await UcitajPregledKontaAsync(selected.Konto);
        }
    }

    private async void BtnGenerisiDocx_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReportFilter(out var konto, out var month, out var year, out var filterError))
        {
            MessageBox.Show(filterError, "Provjera podataka", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                string.Empty,
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
            btnGenerisiDocx.IsEnabled = _odabraniPregled is not null;
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

    private async Task UcitajIzvjestajAsync(
        Konto konto,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        txtIzvjestajStatus.Text = "Učitavanje podataka...";
        _trenutniIzvjestaj = await _ppAparatRepository.GetForReportAsync(
            konto.Sifra,
            month,
            year,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
        konto = _odabraniPregled?.Kupac;
        month = _pregledMjesec;
        year = _pregledGodina;
        error = string.Empty;

        if (konto is null)
        {
            error = "Odaberite kupca iz tabele mjesečne evidencije.";
            return false;
        }

        if (year is < 1900 or > 9999)
        {
            error = "Osvježite pregled za ispravnu godinu.";
            return false;
        }

        if (month is < 1 or > 12)
        {
            error = "Odaberite mjesec.";
            return false;
        }

        return true;
    }

    private bool TryGetOverviewFilter(
        out int month,
        out int year,
        out IzvjestajStatusFilter statusFilter,
        out string error)
    {
        month = (cmbMjesec.SelectedItem as MjesecStavka)?.Broj ?? 0;
        year = cmbGodina.SelectedItem is int selectedYear ? selectedYear : 0;
        statusFilter = (cmbStatusIzvjestaja.SelectedItem as IzvjestajStatusFilterStavka)?.Vrijednost
            ?? IzvjestajStatusFilter.Nezakljuceni;
        error = string.Empty;

        if (month is < 1 or > 12)
        {
            error = "Odaberite mjesec.";
            return false;
        }

        if (year is < 1900 or > 9999)
        {
            error = "Odaberite ispravnu godinu.";
            return false;
        }

        return true;
    }

    private void ClearSelectedReport()
    {
        _detaljiIzvjestajaCancellation?.Cancel();
        _odabraniPregled = null;
        _trenutniIzvjestaj = [];
        dgIzvjestaj.ItemsSource = null;
        pnlOdabraniKupac.DataContext = null;
        pnlOdabraniKupac.Visibility = Visibility.Collapsed;
        pnlBezOdabranogKupca.Visibility = Visibility.Visible;
        txtOdabraniKupac.Text = string.Empty;
        txtOdabraniPeriod.Text = string.Empty;
        txtIzvjestajStatus.Text = string.Empty;
        btnPromijeniStatus.Content = "Zaključi";
        btnPromijeniStatus.ToolTip = null;
        btnPromijeniStatus.IsEnabled = false;
        btnGenerisiDocx.IsEnabled = false;
    }

    private async Task OsvjeziGodineAsync(int newYear)
    {
        var years = (cmbGodina.ItemsSource as IEnumerable<int>)?.ToList() ?? [];
        if (!years.Contains(newYear))
        {
            var selectedYear = cmbGodina.SelectedItem is int currentYear
                ? currentYear
                : DateTime.Today.Year;
            years.Add(newYear);
            years.Sort((left, right) => right.CompareTo(left));
            _reportFiltersReady = false;
            try
            {
                cmbGodina.ItemsSource = years;
                cmbGodina.SelectedItem = selectedYear;
            }
            finally
            {
                _reportFiltersReady = true;
            }
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

    private static IReadOnlyList<IzvjestajStatusFilterStavka> KreirajStatusFiltere() =>
    [
        new(IzvjestajStatusFilter.Nezakljuceni, "Nezaključeni"),
        new(IzvjestajStatusFilter.Zakljuceni, "Zaključeni"),
        new(IzvjestajStatusFilter.Svi, "Svi")
    ];

    private static string FormatirajBrojAparata(int count) => count switch
    {
        1 => "1 aparat",
        _ => $"{count} aparata"
    };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
