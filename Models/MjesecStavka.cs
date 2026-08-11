namespace PpEvidencija.Models;

public sealed record MjesecStavka(int Broj, string Naziv)
{
    public override string ToString() => Naziv;
}
