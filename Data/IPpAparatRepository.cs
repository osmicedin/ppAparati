using PpEvidencija.Models;

namespace PpEvidencija.Data;

public interface IPpAparatRepository
{
    Task<long> InsertAsync(
        PpAparatInput input, string korisnickoIme, CancellationToken cancellationToken = default);
    Task<IzvjestajSnimak> GetForReportAsync(
        long grupaId, long ocekivanaVerzija, CancellationToken cancellationToken = default);
}
