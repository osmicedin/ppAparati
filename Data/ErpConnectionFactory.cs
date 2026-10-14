using Microsoft.Data.SqlClient;
using PpEvidencija.Services;

namespace PpEvidencija.Data;

public sealed class ErpConnectionFactory(string connectionString) : SqlConnectionFactory(connectionString)
{
    public override async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        try { return await base.OpenAsync(cancellationToken); }
        catch (SqlException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DatabaseUnavailableException("ERP baza nije dostupna. Provjerite mrežu i konfiguraciju ERP baze.", ex);
        }
    }
}
