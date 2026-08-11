using System.Windows;
using System.Windows.Input;
using PpEvidencija.Data;
using PpEvidencija.Models;

namespace PpEvidencija.Views;

public partial class LoginWindow : Window
{
    private readonly AuthRepository _authRepository;

    public LoginWindow(AuthRepository authRepository)
    {
        InitializeComponent();
        _authRepository = authRepository;
        Loaded += (_, _) => txtKorisnickoIme.Focus();
    }

    public AutentifikovaniKorisnik? PrijavljeniKorisnik { get; private set; }

    private async void BtnPrijava_Click(object sender, RoutedEventArgs e)
    {
        var username = txtKorisnickoIme.Text.Trim();
        var password = txtLozinka.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            txtStatus.Text = "Unesite korisničko ime i lozinku.";
            return;
        }

        btnPrijava.IsEnabled = false;
        txtStatus.Text = "Provjera prijave...";

        try
        {
            PrijavljeniKorisnik = await _authRepository.AuthenticateAsync(username, password);
            if (PrijavljeniKorisnik is null)
            {
                txtStatus.Text = "Pogrešni podaci ili korisnik nije aktivan.";
                txtLozinka.Clear();
                txtLozinka.Focus();
                return;
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Prijava nije moguća: {ex.Message}";
        }
        finally
        {
            btnPrijava.IsEnabled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
