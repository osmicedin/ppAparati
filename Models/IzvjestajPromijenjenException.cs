namespace PpEvidencija.Models;

public sealed class IzvjestajPromijenjenException : InvalidOperationException
{
    public IzvjestajPromijenjenException()
        : base("Podaci ili status skupa su u međuvremenu promijenjeni. Osvježite pregled i pokušajte ponovo.")
    {
    }
}
