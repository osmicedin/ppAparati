using Microsoft.Data.SqlClient;
using PpEvidencija.Services;

namespace PpEvidencija.Data;

internal static class ErpQuery
{
    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> query, CancellationToken cancellationToken)
    {
        try { return await query(); }
        catch (SqlException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DatabaseUnavailableException(
                "ERP baza nije dostupna ili nije moguće pročitati potrebne podatke. Provjerite konfiguraciju ERP baze.", ex);
        }
    }
}
