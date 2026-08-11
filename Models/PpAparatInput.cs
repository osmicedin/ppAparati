namespace PpEvidencija.Models;

public sealed record PpAparatInput(
    string Konto,
    string Tip,
    decimal PunjenjeKg,
    string SerijskiBroj,
    short GodinaProizvodnje,
    DateTime DatumServisa,
    string KonstatacijaIspravnosti,
    string Vozilo,
    string IspitivanjeIzvrsio)
{
    public DateTime SljedeciServis => DatumServisa.Date.AddMonths(6);
}
