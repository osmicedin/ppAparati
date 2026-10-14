using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;

namespace PpEvidencija.Views;

public partial class MainWindow : Window
{
    private const string DefaultKontoPrefix = "211";

    private readonly AutentifikovaniKorisnik _korisnik;
    private readonly KontoRepository _kontoRepository;
    private readonly IPpAparatRepository _ppAparatRepository;
    private readonly IIzvjestajEvidencijaRepository _izvjestajEvidencijaRepository;
    private readonly PpAparatValidator _validator;
    private readonly IDocxReportService _docxReportService;
    private readonly IReportDialogs _reportDialogs;
    private IzvjestajPregledRed? _odabraniPregled;
    private IzvjestajPregledRed? _odabranaGrupa;
    private string? _zadnjiDocxPath;
    private string _lastInspector = string.Empty;
    private CancellationTokenSource? _kontoUnosSearchCancellation;
    private CancellationTokenSource? _pregledKontaCancellation;
    private CancellationTokenSource? _detaljiIzvjestajaCancellation;
    private CancellationTokenSource? _historijaCancellation;
    private bool _suppressKontoSearch;
    private bool _reportFiltersReady;
    private bool _overviewLoading;
    private bool _reportActionInProgress;
    private bool _historijaLoading;
    private bool _detailsLoading;
    private bool _detailsReady;
    private bool _suppressHistorijaSelection;
    private bool _inputInProgress;

    public MainWindow(
        AutentifikovaniKorisnik korisnik,
        KontoRepository kontoRepository,
        IPpAparatRepository ppAparatRepository,
        IIzvjestajEvidencijaRepository izvjestajEvidencijaRepository,
        PpAparatValidator validator,
        IDocxReportService docxReportService,
        IReportDialogs reportDialogs)
    {
        InitializeComponent();
        _korisnik = korisnik;
        _kontoRepository = kontoRepository;
        _ppAparatRepository = ppAparatRepository;
        _izvjestajEvidencijaRepository = izvjestajEvidencijaRepository;
        _validator = validator;
        _docxReportService = docxReportService;
        _reportDialogs = reportDialogs;

        txtPrijavljeniKorisnik.Text = $"Korisnik: {_korisnik.KorisnickoIme}";
        dpDatumServisa.DisplayDateStart = ServiceDateRange.Minimum;
        dpDatumServisa.DisplayDateEnd = ServiceDateRange.Maximum;
        dpDatumServisa.SelectedDate = DateTime.Today;
        dpDatumZakljucivanja.DisplayDateStart = ServiceDateRange.Minimum;
        txtGodinaProizvodnje.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        txtKonstatacija.Text = "Ispravan";
        txtIspitivanjeIzvrsio.Text = _lastInspector;
        cmbTip.ItemsSource = new[] { "S 1", "S 2", "S 3", "S 6", "S 9", "Co2", "S 50" };

        cmbKontoUnos.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(KontoComboBox_TextChanged));

        cmbStatusIzvjestaja.ItemsSource = KreirajStatusFiltere();
        cmbStatusIzvjestaja.SelectedIndex = 0;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UcitajPocetnePodatkeAsync();
    }

    internal async Task UcitajPocetnePodatkeAsync()
    {
        IsEnabled = false;
        txtGlobalStatus.Text = "Učitavanje početnih podataka...";

        try
        {
            _lastInspector = (await _ppAparatRepository.GetLastInspectorAsync())?.Trim() ?? string.Empty;
            txtIspitivanjeIzvrsio.Text = _lastInspector;
            _reportFiltersReady = true;
            await UcitajPregledKontaAsync();
        }
        catch (Exception ex)
        {
            txtGlobalStatus.Text = "Početni podaci nisu učitani.";
            _reportDialogs.ShowError(this,
                $"Nije moguće učitati početne podatke.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Greška baze");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void BtnSnimi_Click(object sender, RoutedEventArgs e)
    {
        await SnimiAparatAsync();
    }

    internal async Task SnimiAparatAsync()
    {
        if (_inputInProgress || _reportActionInProgress) return;
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

        _inputInProgress = true;
        OsvjeziDostupnostRadnji();
        txtGlobalStatus.Text = "Snimanje PP aparata...";

        try
        {
            var id = await _ppAparatRepository.InsertAsync(input!, _korisnik.KorisnickoIme);
            _lastInspector = input!.IspitivanjeIzvrsio;
            txtUnosStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            txtUnosStatus.Text = $"PP aparat je uspješno snimljen. ID zapisa: {id}.";
            txtGlobalStatus.Text = "Snimanje završeno.";
            OcistiPoljaUnosa(zadrziKonto: true);
            await UcitajPregledKontaAsync(_odabraniPregled);
        }
        catch (Exception ex)
        {
            txtUnosStatus.Text = $"Snimanje nije uspjelo: {ex.Message}";
            txtGlobalStatus.Text = "Greška pri snimanju.";
        }
        finally
        {
            _inputInProgress = false;
            OsvjeziDostupnostRadnji();
        }
    }

    private void BtnOcisti_Click(object sender, RoutedEventArgs e)
    {
        OcistiPoljaUnosa(zadrziKonto: true);
        txtUnosStatus.Text = string.Empty;
    }

    internal void OcistiPoljaUnosa(bool zadrziKonto)
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
        txtIspitivanjeIzvrsio.Text = _lastInspector;
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
        if (_suppressKontoSearch || sender is not ComboBox comboBox || comboBox.Items.Count > 0)
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
                PrikaziRezultateKonta(comboBox, konta, comboBox.IsKeyboardFocusWithin);
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

    internal static void PrikaziRezultateKonta(
        ComboBox comboBox,
        IReadOnlyList<Konto> konta,
        bool openDropDown)
    {
        var enteredText = comboBox.Text;
        var editor = comboBox.Template?.FindName("PART_EditableTextBox", comboBox) as TextBox;
        var selectionStart = editor?.SelectionStart ?? enteredText.Length;
        var selectionLength = editor?.SelectionLength ?? 0;

        comboBox.ItemsSource = konta;
        comboBox.SelectedItem = null;
        comboBox.Text = enteredText;
        if (openDropDown)
        {
            comboBox.IsDropDownOpen = true;
        }

        // WPF selects all text when an editable ComboBox opens; keep the user's caret/selection.
        editor?.Select(selectionStart, selectionLength);
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

        if (!PpInputParser.TryParsePositiveDecimal(txtPunjenje.Text, out punjenje))
        {
            validationErrors.Add("Punjenje mora biti pozitivan broj s najviše dvije decimale. Koristite zarez ili tačku bez separatora hiljada.");
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
            await UcitajPregledKontaAsync(_odabraniPregled);
        }
    }

    private async void BtnOsvjeziPregledKonta_Click(object sender, RoutedEventArgs e)
    {
        await UcitajPregledKontaAsync(_odabraniPregled);
    }

    internal async Task UcitajPregledKontaAsync(IzvjestajPregledRed? zadrziRed = null)
    {
        var statusFilter = (cmbStatusIzvjestaja.SelectedItem as IzvjestajStatusFilterStavka)?.Vrijednost
            ?? IzvjestajStatusFilter.Nezakljuceni;
        var cancellation = new CancellationTokenSource();
        _pregledKontaCancellation?.Cancel();
        _pregledKontaCancellation?.Dispose();
        _pregledKontaCancellation = cancellation;
        var token = cancellation.Token;

        _overviewLoading = true;
        dgKontaIzvjestaji.ItemsSource = null;
        ClearSelectedReport();
        txtPregledKontaStatus.Text = "Učitavanje kupaca...";
        pnlPrazanPregled.Visibility = Visibility.Visible;
        txtPrazanPregled.Text = "Učitavanje kupaca...";

        try
        {
            var rows = await _izvjestajEvidencijaRepository.GetPregledAsync(statusFilter, token);
            token.ThrowIfCancellationRequested();
            dgKontaIzvjestaji.ItemsSource = rows;
            var rowToKeep = zadrziRed is null ? null : rows.FirstOrDefault(row =>
                string.Equals(row.Konto, zadrziRed.Konto, StringComparison.OrdinalIgnoreCase));
            if (rowToKeep is not null)
            {
                dgKontaIzvjestaji.SelectedItem = rowToKeep;
                dgKontaIzvjestaji.ScrollIntoView(rowToKeep);
            }

            txtPregledKontaStatus.Text = rows.Count == 0
                ? "Nema kupaca za odabrani status."
                : $"Prikazano kupaca: {rows.Count}.";
            pnlPrazanPregled.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            txtPrazanPregled.Text = "Nema kupaca za odabrani status.";
            txtGlobalStatus.Text = "Pregled kupaca je osvježen.";
        }
        catch (OperationCanceledException)
        {
            // A newer filter/refresh owns the screen now.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_pregledKontaCancellation, cancellation)) return;
            dgKontaIzvjestaji.ItemsSource = null;
            ClearSelectedReport();
            txtPregledKontaStatus.Text = "Pregled kupaca nije učitan.";
            pnlPrazanPregled.Visibility = Visibility.Visible;
            txtPrazanPregled.Text = "Pregled trenutno nije dostupan.";
            txtGlobalStatus.Text = "Greška pri učitavanju evidencije.";
            _reportDialogs.ShowError(this, $"Nije moguće učitati evidenciju.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Greška baze");
        }
        finally
        {
            if (ReferenceEquals(_pregledKontaCancellation, cancellation))
            {
                _overviewLoading = false;
                OsvjeziDostupnostRadnji();
            }
        }
    }

    private async void DgKontaIzvjestaji_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearSelectedReport();
        if (dgKontaIzvjestaji.SelectedItem is not IzvjestajPregledRed selected) return;
        _odabraniPregled = selected;
        pnlBezOdabranogKupca.Visibility = Visibility.Collapsed;
        pnlOdabraniKupac.Visibility = Visibility.Visible;
        txtOdabraniKupac.Text = selected.Kupac.Prikaz;
        await UcitajHistorijuAsync(selected);
    }

    private async Task UcitajHistorijuAsync(IzvjestajPregledRed selected)
    {
        var cancellation = new CancellationTokenSource();
        _historijaCancellation?.Cancel();
        _historijaCancellation?.Dispose();
        _historijaCancellation = cancellation;
        var token = cancellation.Token;
        _historijaLoading = true;
        txtIzvjestajStatus.Text = "Učitavanje historije...";
        OsvjeziDostupnostRadnji();
        try
        {
            var groups = await _izvjestajEvidencijaRepository.GetHistorijaAsync(selected.Konto, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_odabraniPregled, selected)) return;
            _suppressHistorijaSelection = true;
            try
            {
                cmbHistorijaIzvjestaja.ItemsSource = groups;
                // The repository returns the current group first, then immutable history.
                cmbHistorijaIzvjestaja.SelectedItem = groups.FirstOrDefault();
            }
            finally
            {
                _suppressHistorijaSelection = false;
            }
            _historijaLoading = false;
            if (cmbHistorijaIzvjestaja.SelectedItem is IzvjestajPregledRed group)
            {
                await OdaberiGrupuAsync(group);
            }
            else
            {
                txtIzvjestajStatus.Text = "Konto nema evidentiranih aparata.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_historijaCancellation, cancellation) || token.IsCancellationRequested
                || !ReferenceEquals(_odabraniPregled, selected)) return;
            txtIzvjestajStatus.Text = "Historija nije učitana. Osvježite pregled.";
            _reportDialogs.ShowError(this, ex.Message, "Greška historije");
        }
        finally
        {
            if (ReferenceEquals(_historijaCancellation, cancellation))
            {
                _historijaLoading = false;
                OsvjeziDostupnostRadnji();
            }
        }
    }

    private async void HistorijaIzvjestaja_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorijaSelection) return;
        if (cmbHistorijaIzvjestaja.SelectedItem is IzvjestajPregledRed group)
        {
            await OdaberiGrupuAsync(group);
        }
    }

    private async Task OdaberiGrupuAsync(IzvjestajPregledRed group)
    {
        var cancellation = new CancellationTokenSource();
        _detaljiIzvjestajaCancellation?.Cancel();
        _detaljiIzvjestajaCancellation?.Dispose();
        _detaljiIzvjestajaCancellation = cancellation;
        _odabranaGrupa = group;
        _detailsReady = false;
        _detailsLoading = true;
        dgIzvjestaj.ItemsSource = null;
        dpDatumZakljucivanja.SelectedDate = null;
        pnlOdabraniKupac.DataContext = group;
        txtOdabraniPeriod.Text = $"{group.PeriodPrikaz} • {FormatirajBrojAparata(group.BrojAparata)}";
        btnPromijeniStatus.ToolTip = "Završi trenutni skup aparata. Sljedeći unos automatski otvara novi skup.";
        OsvjeziDostupnostRadnji();
        try
        {
            await UcitajIzvjestajAsync(group, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (IzvjestajPromijenjenException ex)
        {
            if (ReferenceEquals(_detaljiIzvjestajaCancellation, cancellation)
                && !cancellation.IsCancellationRequested && ReferenceEquals(_odabranaGrupa, group))
            {
                await UcitajPregledKontaAsync(_odabraniPregled);
                txtGlobalStatus.Text = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_detaljiIzvjestajaCancellation, cancellation)
                || cancellation.IsCancellationRequested || !ReferenceEquals(_odabranaGrupa, group)) return;
            txtIzvjestajStatus.Text = "Podaci nisu učitani. Osvježite pregled.";
            _reportDialogs.ShowError(this, ex.Message, "Greška baze");
        }
        finally
        {
            if (ReferenceEquals(_detaljiIzvjestajaCancellation, cancellation))
            {
                _detailsLoading = false;
                OsvjeziDostupnostRadnji();
            }
        }
    }

    private async void BtnPromijeniStatus_Click(object sender, RoutedEventArgs e) => await PromijeniStatusAsync();

    internal async Task PromijeniStatusAsync()
    {
        if (!ReportActionsAvailable || _odabranaGrupa is not { Zakljucen: false } selected) return;
        _reportActionInProgress = true;
        OsvjeziDostupnostRadnji();
        try
        {
            if (!_reportDialogs.ConfirmClose(this, selected)) return;
            txtGlobalStatus.Text = "Zaključivanje konta...";
            await _izvjestajEvidencijaRepository.ZakljuciAsync(selected.GrupaId, selected.Verzija, _korisnik.KorisnickoIme);
            await UcitajPregledKontaAsync(selected);
            txtGlobalStatus.Text = "Skup aparata je zaključen. Novi unos automatski otvara novi skup.";
        }
        catch (IzvjestajPromijenjenException ex)
        {
            await UcitajPregledKontaAsync(selected);
            txtGlobalStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _reportDialogs.ShowError(this, $"Konto nije zaključen.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Zaključivanje konta");
            await UcitajPregledKontaAsync(selected);
        }
        finally
        {
            _reportActionInProgress = false;
            OsvjeziDostupnostRadnji();
        }
    }

    private async void BtnGenerisiDocx_Click(object sender, RoutedEventArgs e) => await GenerisiDocxAsync();

    internal async Task GenerisiDocxAsync()
    {
        if (!ReportActionsAvailable || _odabranaGrupa is not IzvjestajPregledRed selected) return;
        if (dpDatumZakljucivanja.SelectedDate is not DateTime datumZakljucivanja)
        {
            txtIzvjestajStatus.Text = "Odaberite datum dokumenta.";
            return;
        }
        _reportActionInProgress = true;
        OsvjeziDostupnostRadnji();
        txtIzvjestajStatus.Text = "Priprema izvještaja...";
        try
        {
            var snapshot = await _ppAparatRepository.GetForReportAsync(selected.GrupaId, selected.Verzija);
            var aparati = snapshot.Aparati.ToArray();
            if (aparati.Length == 0)
            {
                txtIzvjestajStatus.Text = "Izabrani skup nema aparata.";
                return;
            }
            var datumNaziva = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var outputPath = _reportDialogs.ChooseOutputPath(this,
                $"aparati_{SafeFileName(snapshot.Grupa.Konto)}_{datumNaziva}_{snapshot.Grupa.GrupaId}.docx");
            if (outputPath is null)
            {
                txtIzvjestajStatus.Text = "Generisanje je otkazano.";
                return;
            }
            var request = new IzvjestajZahtjev(string.Empty, datumZakljucivanja.Date,
                snapshot.Grupa.Kupac, snapshot.Grupa.GrupaId, aparati);
            await _docxReportService.GenerateAsync(request, outputPath);
            _zadnjiDocxPath = outputPath;
            btnOtvoriWord.IsEnabled = true;
            txtIzvjestajStatus.Text = $"Dokument je sačuvan: {Path.GetFileName(outputPath)}";
            txtGlobalStatus.Text = "DOCX izvještaj je generisan.";
        }
        catch (IzvjestajPromijenjenException ex)
        {
            await UcitajPregledKontaAsync(selected);
            txtGlobalStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            txtIzvjestajStatus.Text = "Generisanje nije uspjelo.";
            _reportDialogs.ShowError(this, $"Nije moguće generisati DOCX.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Greška izvještaja");
        }
        finally
        {
            _reportActionInProgress = false;
            OsvjeziDostupnostRadnji();
        }
    }

    private void BtnOtvoriWord_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_zadnjiDocxPath) || !File.Exists(_zadnjiDocxPath))
        {
            _reportDialogs.ShowError(this, "Generisani dokument više nije dostupan.", "Dokument");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _zadnjiDocxPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _reportDialogs.ShowError(this,
                $"Dokument je sačuvan, ali ga nije moguće otvoriti. Provjerite da li je Word instaliran.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Otvaranje dokumenta");
        }
    }

    internal async Task UcitajIzvjestajAsync(IzvjestajPregledRed selected, CancellationToken cancellationToken = default)
    {
        txtIzvjestajStatus.Text = "Učitavanje podataka...";
        var snapshot = await _ppAparatRepository.GetForReportAsync(selected.GrupaId, selected.Verzija, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_odabranaGrupa, selected)) return;
        dgIzvjestaj.ItemsSource = snapshot.Aparati.Select((item, index) => new IzvjestajRed(index + 1, item)).ToList();
        dpDatumZakljucivanja.SelectedDate = snapshot.Aparati.Max(item => (DateTime?)item.DatumServisa.Date);
        _detailsReady = snapshot.Aparati.Count > 0;
        txtIzvjestajStatus.Text = $"Pronađeno zapisa: {snapshot.Aparati.Count}.";
    }

    private void ClearSelectedReport()
    {
        _historijaCancellation?.Cancel();
        _detaljiIzvjestajaCancellation?.Cancel();
        _odabraniPregled = null;
        _odabranaGrupa = null;
        _historijaLoading = false;
        _detailsLoading = false;
        _detailsReady = false;
        _suppressHistorijaSelection = true;
        try { cmbHistorijaIzvjestaja.ItemsSource = null; }
        finally { _suppressHistorijaSelection = false; }
        dgIzvjestaj.ItemsSource = null;
        pnlOdabraniKupac.DataContext = null;
        dpDatumZakljucivanja.SelectedDate = null;
        pnlOdabraniKupac.Visibility = Visibility.Collapsed;
        pnlBezOdabranogKupca.Visibility = Visibility.Visible;
        txtOdabraniKupac.Text = string.Empty;
        txtOdabraniPeriod.Text = string.Empty;
        txtIzvjestajStatus.Text = string.Empty;
        btnPromijeniStatus.ToolTip = null;
        OsvjeziDostupnostRadnji();
    }

    private bool ReportActionsAvailable => !_overviewLoading && !_reportActionInProgress
        && !_inputInProgress && !_historijaLoading && !_detailsLoading && _detailsReady;

    private void OsvjeziDostupnostRadnji()
    {
        var available = !_overviewLoading && !_reportActionInProgress && !_inputInProgress;
        btnPromijeniStatus.IsEnabled = ReportActionsAvailable && _odabranaGrupa is { Zakljucen: false };
        btnGenerisiDocx.IsEnabled = ReportActionsAvailable && _odabranaGrupa is not null;
        btnOsvjeziPregledKonta.IsEnabled = available;
        dgKontaIzvjestaji.IsEnabled = available;
        cmbHistorijaIzvjestaja.IsEnabled = available && !_historijaLoading;
        cmbStatusIzvjestaja.IsEnabled = !_reportActionInProgress && !_inputInProgress;
        dpDatumZakljucivanja.IsEnabled = !_reportActionInProgress;
        btnSnimi.IsEnabled = !_inputInProgress && !_reportActionInProgress;
    }
    private void DpDatumServisa_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (dpDatumServisa.SelectedDate is DateTime selected && !ServiceDateRange.Contains(selected))
        {
            OdbaciNeispravanDatum(dpDatumServisa);
            return;
        }

        txtSljedeciServis.Text = dpDatumServisa.SelectedDate is DateTime date
            && ServiceDateRange.Contains(date)
            ? date.AddMonths(6).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private void DpDatumZakljucivanja_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dpDatumZakljucivanja.SelectedDate is DateTime selected && selected < ServiceDateRange.Minimum)
        {
            OdbaciNeispravanDatum(dpDatumZakljucivanja);
        }
    }

    private void Datum_DateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        if (sender is DatePicker picker)
        {
            OdbaciNeispravanDatum(picker);
        }
    }

    private void OdbaciNeispravanDatum(DatePicker picker)
    {
        picker.SelectedDate = null;
        picker.Text = string.Empty;
        picker.DisplayDateStart = ServiceDateRange.Minimum;

        if (ReferenceEquals(picker, dpDatumServisa))
        {
            picker.DisplayDateEnd = ServiceDateRange.Maximum;
            txtUnosStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            txtUnosStatus.Text = "Unesite ispravan datum servisa (dd.MM.yyyy) između 01.01.1900. i 30.06.9999.";
        }
        else
        {
            txtIzvjestajStatus.Text = "Unesite ispravan datum dokumenta (dd.MM.yyyy), od 01.01.1900.";
        }
    }

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
