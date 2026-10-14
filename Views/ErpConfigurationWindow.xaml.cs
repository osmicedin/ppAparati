using System.Windows;
using System.Windows.Media;
using PpEvidencija.Models;
using PpEvidencija.Services;

namespace PpEvidencija.Views;

public partial class ErpConfigurationWindow : Window
{
    private readonly IErpConfigurationService _configuration;
    private byte[]? _expectedVersion;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _closed;
    private bool _loaded;

    public ErpConfigurationWindow(IErpConfigurationService configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        SetBusy(false);
        txtStatus.Text = "Učitavanje ERP postavki...";
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += async (_, _) =>
        {
            if (await LoadAsync() && !_closed) txtServer.Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            txtPassword.Clear();
            if (!_busy) _lifetime.Dispose();
        };
    }

    internal async Task<bool> LoadAsync()
    {
        if (_busy || _closed) return false;
        if (_loaded) return true;
        var token = _lifetime.Token;
        SetBusy(true);
        btnPonovoUcitaj.Visibility = Visibility.Collapsed;
        txtStatus.Text = "Učitavanje ERP postavki...";
        try
        {
            var metadata = await _configuration.GetMetadataAsync(token);
            token.ThrowIfCancellationRequested();
            _expectedVersion = metadata?.Version.ToArray();
            txtServer.Text = metadata?.ServerName ?? string.Empty;
            txtDatabase.Text = metadata?.DatabaseName ?? string.Empty;
            txtUser.Text = metadata?.UserName ?? string.Empty;
            txtPassword.Clear();
            txtPasswordHint.Text = "Unesite SQL lozinku za testiranje i spremanje postavki. Spremljena lozinka se ne prikazuje.";
            _loaded = true;
            txtStatus.Text = string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                txtStatus.Text = DatabaseErrorMessages.ForUser(ex,
                    "ERP postavke nisu učitane. Provjerite vezu s bazom evidencija i pokušajte ponovo.");
                btnPonovoUcitaj.Visibility = Visibility.Visible;
            }
            return false;
        }
        finally
        {
            FinishOperation();
        }
    }

    private async void BtnPonovoUcitaj_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void BtnTest_Click(object sender, RoutedEventArgs e) => await TestAsync();

    private async void BtnSacuvaj_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync() && !_closed) DialogResult = true;
    }

    internal Task<bool> TestAsync() => RunAsync(save: false);
    internal Task<bool> SaveAsync() => RunAsync(save: true);

    private async Task<bool> RunAsync(bool save)
    {
        if (_busy || _closed || !_loaded) return false;
        txtStatus.Foreground = (Brush)FindResource("DangerBrush");
        if (!TryBuildInput(out var input)) return false;

        var token = _lifetime.Token;
        SetBusy(true);
        txtStatus.Text = save ? "Provjera i spremanje postavki..." : "Provjera ERP konekcije i potrebnih tabela...";
        try
        {
            if (save) await _configuration.SaveAsync(input!, _expectedVersion, token);
            else await _configuration.TestAsync(input!, token);
            token.ThrowIfCancellationRequested();
            txtStatus.Foreground = Brushes.DarkGreen;
            txtStatus.Text = save
                ? "ERP postavke su sačuvane. Koristit će se pri narednoj prijavi."
                : "Konekcija je uspješna. Kliknite Sačuvaj za spremanje postavki.";
            if (save) txtPassword.Clear();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!_closed)
                txtStatus.Text = DatabaseErrorMessages.ForUser(ex, save
                    ? "ERP postavke nisu sačuvane. Provjerite konekciju i pokušajte ponovo."
                    : "ERP konekcija nije uspješna. Provjerite podatke i pristup potrebnim tabelama.");
            return false;
        }
        finally
        {
            FinishOperation();
        }
    }

    private bool TryBuildInput(out ErpConfigurationInput? input)
    {
        input = null;
        if (string.IsNullOrWhiteSpace(txtServer.Text)
            || string.IsNullOrWhiteSpace(txtDatabase.Text)
            || string.IsNullOrWhiteSpace(txtUser.Text))
        {
            txtStatus.Text = "Unesite SQL instancu, naziv baze i SQL korisničko ime.";
            return false;
        }
        var password = txtPassword.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            txtStatus.Text = "Unesite SQL lozinku prije testiranja ili spremanja postavki.";
            txtPassword.Focus();
            return false;
        }

        input = new ErpConfigurationInput(txtServer.Text.Trim(), txtDatabase.Text.Trim(),
            txtUser.Text.Trim(), password);
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        pnlPolja.IsEnabled = !busy && _loaded;
        btnTest.IsEnabled = !busy && _loaded;
        btnSacuvaj.IsEnabled = !busy && _loaded;
        btnPonovoUcitaj.IsEnabled = !busy;
    }

    private void FinishOperation()
    {
        if (_closed)
        {
            _busy = false;
            _lifetime.Dispose();
        }
        else SetBusy(false);
    }

    private void BtnOdustani_Click(object sender, RoutedEventArgs e) => Close();
}
