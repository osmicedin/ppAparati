using System.Windows;
using System.Windows.Input;
using PpEvidencija.Data;
using PpEvidencija.Models;
using PpEvidencija.Services;

namespace PpEvidencija.Views;

public partial class LoginWindow : Window
{
    private readonly IErpConfigurationService _configuration;
    private readonly Func<ErpConnectionSnapshot, string, string, CancellationToken, Task<AutentifikovaniKorisnik?>> _authenticate;
    private readonly Func<LoginWindow, ErpConfigurationWindow, bool?> _showConfiguration;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _closed;

    public LoginWindow(IErpConfigurationService configuration)
        : this(configuration, (session, username, password, token) =>
            new AuthRepository(session.ConnectionFactory).AuthenticateAsync(username, password, token))
    {
    }

    internal LoginWindow(
        IErpConfigurationService configuration,
        Func<ErpConnectionSnapshot, string, string, CancellationToken, Task<AutentifikovaniKorisnik?>> authenticate,
        Func<LoginWindow, ErpConfigurationWindow, bool?>? showConfiguration = null)
    {
        InitializeComponent();
        _configuration = configuration;
        _authenticate = authenticate;
        _showConfiguration = showConfiguration ?? ((owner, dialog) =>
        {
            dialog.Owner = owner;
            return dialog.ShowDialog();
        });
        Loaded += (_, _) => txtKorisnickoIme.Focus();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            txtLozinka.Clear();
            if (!_busy) _lifetime.Dispose();
        };
    }

    public AutentifikovaniKorisnik? PrijavljeniKorisnik { get; private set; }
    public ErpConnectionSnapshot? ErpSession { get; private set; }

    private async void BtnPrijava_Click(object sender, RoutedEventArgs e)
    {
        if (await TryLoginAsync() && !_closed) DialogResult = true;
    }

    internal async Task<bool> TryLoginAsync()
    {
        if (_busy || _closed) return false;
        var username = txtKorisnickoIme.Text.Trim();
        var password = txtLozinka.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            txtStatus.Text = "Unesite korisničko ime i lozinku.";
            return false;
        }

        var token = _lifetime.Token;
        SetBusy(true);
        PrijavljeniKorisnik = null;
        ErpSession = null;
        txtStatus.Text = "Provjera prijave...";

        try
        {
            var session = await _configuration.OpenSessionAsync(token);
            token.ThrowIfCancellationRequested();
            var user = await _authenticate(session, username, password, token);
            token.ThrowIfCancellationRequested();
            if (user is null)
            {
                txtStatus.Text = "Pogrešni podaci ili korisnik nije aktivan.";
                txtLozinka.Clear();
                txtLozinka.Focus();
                return false;
            }

            PrijavljeniKorisnik = user;
            ErpSession = session;
            txtLozinka.Clear();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!_closed)
                txtStatus.Text = DatabaseErrorMessages.ForUser(ex,
                    "Prijava nije moguća. Provjerite dostupnost i konfiguraciju ERP baze.");
            return false;
        }
        finally
        {
            FinishOperation();
        }
    }

    private void BtnErpKonfiguracija_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        SetBusy(true);
        try
        {
            var dialog = new ErpConfigurationWindow(_configuration);
            txtStatus.Text = string.Empty;
            if (_showConfiguration(this, dialog) == true && !_closed)
                txtStatus.Text = "ERP postavke su sačuvane. Možete se prijaviti.";
        }
        catch (Exception ex)
        {
            if (!_closed)
                txtStatus.Text = DatabaseErrorMessages.ForUser(ex,
                    "ERP postavke nije moguće otvoriti. Provjerite vezu s bazom evidencija.");
        }
        finally
        {
            FinishOperation();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnPrijava.IsEnabled = !busy;
        btnErpKonfiguracija.IsEnabled = !busy;
        txtKorisnickoIme.IsEnabled = !busy;
        txtLozinka.IsEnabled = !busy;
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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
