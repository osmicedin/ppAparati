namespace PpEvidencija.Models;

public enum IzvjestajStatusFilter
{
    Nezakljuceni = 0,
    Zakljuceni = 1,
    Svi = 2
}

public sealed record IzvjestajStatusFilterStavka(
    IzvjestajStatusFilter Vrijednost,
    string Naziv)
{
    public override string ToString() => Naziv;
}
