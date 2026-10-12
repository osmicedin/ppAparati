using PpEvidencija.Models;

namespace PpEvidencija.Data;

public interface IIzvjestajEvidencijaRepository
{
    Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(
        int month, int year, IzvjestajStatusFilter statusFilter, CancellationToken cancellationToken = default);
    Task PromijeniStatusAsync(
        string konto, int month, int year, bool ocekivanoZakljucen, bool zakljucen,
        string korisnickoIme, CancellationToken cancellationToken = default);
}
