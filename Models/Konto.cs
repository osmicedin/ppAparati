namespace PpEvidencija.Models;

public sealed record Konto(string Sifra, string Naziv)
{
    public string Prikaz => $"{Sifra} - {Naziv}";
}
