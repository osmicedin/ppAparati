using PpEvidencija.Models;

namespace PpEvidencija.Data;

public interface IPpAparatRepository
{
    Task<string?> GetLastInspectorAsync(CancellationToken cancellationToken = default);
    Task<long> InsertAsync(
        PpAparatInput input, string korisnickoIme, CancellationToken cancellationToken = default);
    Task<IzvjestajSnimak> GetForReportAsync(
        long grupaId, long ocekivanaVerzija, CancellationToken cancellationToken = default);
}
