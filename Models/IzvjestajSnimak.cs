namespace PpEvidencija.Models;

public sealed record IzvjestajSnimak(
    IzvjestajPregledRed Grupa,
    IReadOnlyList<PpAparatRecord> Aparati);
