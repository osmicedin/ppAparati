using PpEvidencija.Models;

namespace PpEvidencija.Data;

public interface IPpAparatRepository
{
    Task<long> InsertAsync(PpAparatInput input, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PpAparatRecord>> GetForReportAsync(
        string konto, int month, int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetAvailableYearsAsync(CancellationToken cancellationToken = default);
}
