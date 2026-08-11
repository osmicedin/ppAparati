using PpEvidencija.Models;

namespace PpEvidencija.Services;

public interface IDocxReportService
{
    Task GenerateAsync(
        IzvjestajZahtjev request,
        string outputPath,
        CancellationToken cancellationToken = default);
}
