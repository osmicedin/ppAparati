using PpEvidencija.Data;

namespace PpEvidencija.Models;

public sealed class ErpConfigurationMetadata
{
    public string ServerName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public byte[] Version { get; init; } = [];
    public DateTimeOffset ChangedUtc { get; init; }
}

// Deliberately not a record: generated ToString must never include a password.
public sealed class ErpConfigurationInput(string serverName, string databaseName, string userName, string? password)
{
    public string ServerName { get; } = serverName;
    public string DatabaseName { get; } = databaseName;
    public string UserName { get; } = userName;
    public string? Password { get; } = password;
}

public sealed class ErpConnectionSnapshot(string connectionString, byte[] version)
{
    private readonly byte[] _version = version.ToArray();
    public ErpConnectionFactory ConnectionFactory { get; } = new(connectionString);
    public byte[] Version => _version.ToArray();
}
