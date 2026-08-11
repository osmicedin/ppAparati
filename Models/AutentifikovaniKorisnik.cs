namespace PpEvidencija.Models;

public sealed record AutentifikovaniKorisnik(
    string Id,
    string KorisnickoIme,
    string Ime,
    string Prezime)
{
    public string PunoIme
    {
        get
        {
            var punoIme = $"{Ime.Trim()} {Prezime.Trim()}".Trim();
            return string.IsNullOrWhiteSpace(punoIme) ? KorisnickoIme : punoIme;
        }
    }
}
