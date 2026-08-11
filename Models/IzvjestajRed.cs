namespace PpEvidencija.Models;

public sealed record IzvjestajRed(int RedniBroj, PpAparatRecord Aparat)
{
    public string Tip => Aparat.Tip;
    public decimal PunjenjeKg => Aparat.PunjenjeKg;
    public string SerijskiBroj => Aparat.SerijskiBroj;
    public short GodinaProizvodnje => Aparat.GodinaProizvodnje;
    public DateTime DatumServisa => Aparat.DatumServisa;
    public DateTime SljedeciServis => Aparat.SljedeciServis;
    public string KonstatacijaIspravnosti => Aparat.KonstatacijaIspravnosti;
    public string Vozilo => Aparat.Vozilo;
    public string IspitivanjeIzvrsio => Aparat.IspitivanjeIzvrsio;
}
