namespace PpEvidencija.Models;

public sealed record IzvjestajZahtjev(
    string BrojZapisnika,
    DateTime DatumZakljucivanja,
    Konto Kupac,
    long GrupaId,
    IReadOnlyList<PpAparatRecord> Aparati);
