using PpEvidencija.Models;

namespace PpEvidencija.Services;

public sealed class PpAparatValidator
{
    public IReadOnlyList<string> Validate(PpAparatInput input)
    {
        var errors = new List<string>();

        Required(input.Konto, "Konto je obavezan.", errors);
        Required(input.Tip, "Tip PP aparata je obavezan.", errors);
        Required(input.SerijskiBroj, "Serijski broj je obavezan.", errors);
        Required(input.KonstatacijaIspravnosti, "Konstatacija ispravnosti je obavezna.", errors);
        Required(input.Vozilo, "Vozilo je obavezno. Ako nije primjenjivo unesite N/A.", errors);
        Required(input.IspitivanjeIzvrsio, "Polje 'Ispitivanje izvršio' je obavezno.", errors);

        if (input.PunjenjeKg <= 0 || input.PunjenjeKg > 999999.99m)
        {
            errors.Add("Punjenje mora biti broj veći od 0 i manji od 1.000.000 kg.");
        }

        if (input.GodinaProizvodnje < 1900 || input.GodinaProizvodnje > DateTime.Today.Year)
        {
            errors.Add($"Godina proizvodnje mora biti između 1900 i {DateTime.Today.Year}.");
        }

        if (input.DatumServisa == default)
        {
            errors.Add("Datum servisa je obavezan.");
        }

        MaxLength(input.Konto, 20, "Konto", errors);
        MaxLength(input.Tip, 50, "Tip PP aparata", errors);
        MaxLength(input.SerijskiBroj, 100, "Serijski broj", errors);
        MaxLength(input.KonstatacijaIspravnosti, 100, "Konstatacija ispravnosti", errors);
        MaxLength(input.Vozilo, 100, "Vozilo", errors);
        MaxLength(input.IspitivanjeIzvrsio, 150, "Ispitivanje izvršio", errors);

        return errors;
    }

    private static void Required(string value, string message, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(message);
        }
    }

    private static void MaxLength(string value, int maxLength, string fieldName, ICollection<string> errors)
    {
        if (value.Trim().Length > maxLength)
        {
            errors.Add($"{fieldName} ne smije imati više od {maxLength} znakova.");
        }
    }
}
