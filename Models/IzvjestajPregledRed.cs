using System.Globalization;

namespace PpEvidencija.Models;

public sealed class IzvjestajPregledRed
{
    public string Konto { get; init; } = string.Empty;
    public string NazivKupca { get; init; } = string.Empty;
    public int BrojAparata { get; init; }
    public bool Zakljucen { get; init; }
    public string PosljednjaRadnjaKod { get; init; } = string.Empty;
    public string PromijenioKorisnik { get; init; } = string.Empty;
    public DateTimeOffset? PromijenjenoUtc { get; init; }

    public string Status => Zakljucen ? "Zaključen" : "Nezaključen";

    public string PosljednjaRadnja => PosljednjaRadnjaKod switch
    {
        "Z" => "Zaključeno",
        "O" => "Ponovo otvoreno",
        _ => string.Empty
    };

    public string PromijenjenoPrikaz => PromijenjenoUtc is DateTimeOffset vrijeme
        ? vrijeme.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    public string PosljednjaPromjenaPrikaz => string.IsNullOrWhiteSpace(PosljednjaRadnja)
        ? "Nije mijenjano"
        : $"{PosljednjaRadnja} · {PromijenioKorisnik} · {PromijenjenoPrikaz}";

    public Konto Kupac => new(Konto, NazivKupca);
}
