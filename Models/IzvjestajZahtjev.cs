namespace PpEvidencija.Models;

public sealed record IzvjestajZahtjev(
    string BrojZapisnika,
    DateTime DatumZakljucivanja,
    Konto Kupac,
    int Mjesec,
    int Godina,
    IReadOnlyList<PpAparatRecord> Aparati);
