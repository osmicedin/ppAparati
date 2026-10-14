using System.Globalization;

namespace PpEvidencija.Models;

public sealed class IzvjestajPregledRed
{
    public long GrupaId { get; init; }
    public long Verzija { get; init; }
    public string Konto { get; init; } = string.Empty;
    public string NazivKupca { get; set; } = string.Empty;
    public DateTime DatumOd { get; init; }
    public DateTime DatumDo { get; init; }
    public int BrojAparata { get; init; }
    public bool Zakljucen { get; init; }
    public string PosljednjaRadnjaKod { get; init; } = string.Empty;
    public string PromijenioKorisnik { get; init; } = string.Empty;
    public DateTimeOffset? PromijenjenoUtc { get; init; }
    public DateTimeOffset? ZakljucenoUtc { get; init; }

    public string Status => Zakljucen ? "Zaključen" : "Nezaključen";
    public string PeriodPrikaz => $"{DatumOd:dd.MM.yyyy.} – {DatumDo:dd.MM.yyyy.}";

    public string GrupaPrikaz => Zakljucen
        ? $"Zaključeno {ZakljucenoUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · {BrojAparataPrikaz} · #{GrupaId}"
        : $"Trenutni otvoreni skup · {BrojAparataPrikaz} · #{GrupaId}";

    private string BrojAparataPrikaz => BrojAparata == 1 ? "1 aparat" : $"{BrojAparata} aparata";

    public string PosljednjaRadnja => PosljednjaRadnjaKod switch
    {
        "Z" => "Zaključeno",
        "O" => "Otvoreno",
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
