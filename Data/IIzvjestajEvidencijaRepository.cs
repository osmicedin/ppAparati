using PpEvidencija.Models;

namespace PpEvidencija.Data;

public interface IIzvjestajEvidencijaRepository
{
    Task<IReadOnlyList<IzvjestajPregledRed>> GetPregledAsync(
        IzvjestajStatusFilter statusFilter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IzvjestajPregledRed>> GetHistorijaAsync(
        string konto, CancellationToken cancellationToken = default);
    Task ZakljuciAsync(
        long grupaId, long ocekivanaVerzija,
        string korisnickoIme, CancellationToken cancellationToken = default);
}
