using PpEvidencija.Models;

namespace PpEvidencija.Services;

public interface IErpConfigurationService
{
    Task<ErpConfigurationMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task TestAsync(ErpConfigurationInput input, CancellationToken cancellationToken = default);
    Task SaveAsync(ErpConfigurationInput input, byte[]? expectedVersion, CancellationToken cancellationToken = default);
    Task<ErpConnectionSnapshot> OpenSessionAsync(CancellationToken cancellationToken = default);
}
