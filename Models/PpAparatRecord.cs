namespace PpEvidencija.Models;

public sealed class PpAparatRecord
{
    public long Id { get; init; }
    public string Konto { get; init; } = string.Empty;
    public string Tip { get; init; } = string.Empty;
    public decimal PunjenjeKg { get; init; }
    public string SerijskiBroj { get; init; } = string.Empty;
    public short GodinaProizvodnje { get; init; }
    public DateTime DatumServisa { get; init; }
    public DateTime SljedeciServis { get; init; }
    public string KonstatacijaIspravnosti { get; init; } = string.Empty;
    public string Vozilo { get; init; } = string.Empty;
    public string IspitivanjeIzvrsio { get; init; } = string.Empty;
}
