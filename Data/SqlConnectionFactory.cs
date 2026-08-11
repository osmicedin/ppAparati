using Microsoft.Data.SqlClient;

namespace PpEvidencija.Data;

public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
