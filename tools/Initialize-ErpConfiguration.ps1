#requires -Version 7.2
<#
.SYNOPSIS
Export ERP recovery keys and provision a restricted runtime login using Windows SQL administration credentials.
.DESCRIPTION
Optional administrative setup; Configure_erp.sql already prepares encrypted configuration saving.
Creates no ERP settings and never prints passwords. The target evidencija database and
PP tables must already exist. Run from a trusted administrator workstation. RecoveryDirectory
is an EXISTING directory on the SQL SERVER, writable by its service account; it is not
necessarily a directory on this workstation. Keep its encrypted recovery files separately
from database backups. Existing master keys, certificates, data and logins are not replaced.

For unattended use supply a SecureString value for RecoveryPassword.
Existing runtime logins require their current RuntimePassword; this script never rotates it.
.EXAMPLE
./tools/Initialize-ErpConfiguration.ps1 -ServerInstance 's-office1\sqlexpress' -TrustServerCertificate
.EXAMPLE
./tools/Initialize-ErpConfiguration.ps1 -RuntimePassword (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$ServerInstance = 's-office1\sqlexpress',
    [ValidateLength(1, 128)][string]$DatabaseName = 'evidencija',
    [ValidateLength(1, 128)][string]$RuntimeLogin = 'ppEvidencija_app',
    [System.Security.SecureString]$RecoveryPassword,
    [System.Security.SecureString]$ExistingMasterKeyPassword,
    [System.Security.SecureString]$RuntimePassword,
    [string]$RecoveryDirectory,
    [string]$SettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.json'),
    [switch]$TrustServerCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stage = 'checking arguments'
$adminConnection = $null
$runtimeConnection = $null
$recoveryText = $null
$existingKeyText = $null
$runtimeText = $null
$temporarySettings = $null

function Get-SecretText([System.Security.SecureString]$Secret) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Read-NewSecret([string]$Label) {
    $secret = Read-Host $Label -AsSecureString
    $confirmation = Read-Host 'Repeat password' -AsSecureString
    $first = Get-SecretText $secret
    $second = Get-SecretText $confirmation
    try {
        if (-not [string]::Equals($first, $second, [StringComparison]::Ordinal)) {
            throw 'Password confirmation does not match.'
        }
        return $secret
    }
    finally { $first = $null; $second = $null; $confirmation.Dispose() }
}

function Invoke-Sql([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql,
                    [hashtable]$Parameters = @{}, [switch]$Scalar) {
    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 60
    $command.CommandText = $Sql
    foreach ($entry in $Parameters.GetEnumerator()) {
        # An if-expression would enumerate byte[] into object[], breaking binary SQL parameters.
        $value = $entry.Value
        if ($null -eq $value) { $value = [DBNull]::Value }
        $null = $command.Parameters.AddWithValue($entry.Key, $value)
    }
    try {
        if ($Scalar) { return ,$command.ExecuteScalar() }
        $null = $command.ExecuteNonQuery()
    }
    finally { $command.Parameters.Clear(); $command.Dispose() }
}

function Get-SchemaBody([string]$Path, [string]$Marker) {
    $script = [IO.File]::ReadAllText($Path)
    $start = $script.IndexOf($Marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw 'Required schema boundary is missing.' }
    $end = $script.IndexOf('*/', $start + $Marker.Length, [StringComparison]::Ordinal)
    if ($end -lt 0) { throw 'Required schema boundary is incomplete.' }
    return $script.Substring($end + 2)
}

try {
    if ($DatabaseName -in @('master', 'model', 'msdb', 'tempdb')) {
        throw 'Select the application database, not a system database.'
    }
    $settingsFullPath = [IO.Path]::GetFullPath($SettingsPath)
    if ([IO.Path]::GetFileName($settingsFullPath) -ne 'appsettings.json') {
        throw 'SettingsPath must identify the local ignored appsettings.json file.'
    }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($settingsFullPath))) {
        throw 'The settings directory does not exist.'
    }
    $settings = if ([IO.File]::Exists($settingsFullPath)) {
        Get-Content -LiteralPath $settingsFullPath -Raw | ConvertFrom-Json -AsHashtable
    } else { @{} }
    if (-not $settings.ContainsKey('ConnectionStrings')) { $settings['ConnectionStrings'] = @{} }

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = $ServerInstance
    $builder['Initial Catalog'] = $DatabaseName
    $builder['Integrated Security'] = $true
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $TrustServerCertificate.IsPresent
    $builder['Connect Timeout'] = 10
    $stage = 'opening the Windows administrator connection'
    $adminConnection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $adminConnection.Open()
    $allowed = Invoke-Sql $adminConnection "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR IS_MEMBER('db_owner')=1 THEN 1 ELSE 0 END" -Scalar
    if ($allowed -ne 1) { throw 'Windows SQL sysadmin or database-owner access is required.' }
    if ((Invoke-Sql $adminConnection "SELECT CONVERT(int,SERVERPROPERTY('IsIntegratedSecurityOnly'))" -Scalar) -ne 0) {
        throw 'The dedicated runtime account requires SQL Server mixed authentication mode.'
    }
    $stage = 'checking encryption-recovery inputs'
    if ($null -eq $RecoveryPassword) { $RecoveryPassword = Read-NewSecret 'Recovery-file password (12 to 128 characters)' }
    $recoveryText = Get-SecretText $RecoveryPassword
    if ($recoveryText.Length -lt 12 -or $recoveryText.Length -gt 128) {
        throw 'Use 12 to 128 characters for the recovery-file password.'
    }
    if ([string]::IsNullOrWhiteSpace($RecoveryDirectory)) {
        $RecoveryDirectory = Read-Host 'Existing recovery-backup directory on the SQL Server (writable by its service account)'
    }
    if (-not [IO.Path]::IsPathRooted($RecoveryDirectory)) { throw 'RecoveryDirectory must be an absolute SQL-server-local or UNC path.' }
    if ($null -ne $ExistingMasterKeyPassword) { $existingKeyText = Get-SecretText $ExistingMasterKeyPassword }

    if ($null -ne $existingKeyText) {
        # Recovery of a password-protected existing DMK must precede schema readiness checks.
        $stage = 'opening the existing database master key'
        Invoke-Sql $adminConnection @'
DECLARE @sql nvarchar(max);
IF EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name=N'##MS_DatabaseMasterKey##')
BEGIN
    SET @sql=N'OPEN MASTER KEY DECRYPTION BY PASSWORD=N''' + REPLACE(@ExistingPassword,N'''',N'''''') + N''';';
    EXEC sys.sp_executesql @sql;
    IF (SELECT is_master_key_encrypted_by_server FROM sys.databases WHERE database_id=DB_ID())=0
        ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY;
END;
'@ @{ '@ExistingPassword' = $existingKeyText }
    }
    $sourceSchema = Get-SchemaBody (Join-Path $PSScriptRoot '../Sql/Configure_erp.sql') '/* ERP_SCHEMA_BEGIN:'
    $stage = 'installing ERP procedures and verifying automatic encryption setup'
    foreach ($batch in [regex]::Split($sourceSchema, '(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$')) {
        if (-not [string]::IsNullOrWhiteSpace($batch)) { Invoke-Sql $adminConnection $batch }
    }

    $stage = 'backing up encryption recovery material'
    $backupStem = 'ppEvidencija-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $masterBackup = [IO.Path]::Combine($RecoveryDirectory, $backupStem + '.masterkey')
    $certificateBackup = [IO.Path]::Combine($RecoveryDirectory, $backupStem + '.cer')
    $privateBackup = [IO.Path]::Combine($RecoveryDirectory, $backupStem + '.pvk')
    Invoke-Sql $adminConnection @'
DECLARE @secret nvarchar(max)=N'N''' + REPLACE(@Password,N'''',N'''''') + N'''';
DECLARE @sql nvarchar(max)=N'BACKUP MASTER KEY TO FILE=N''' + REPLACE(@MasterPath,N'''',N'''''')
    + N''' ENCRYPTION BY PASSWORD=' + @secret + N';';
EXEC sys.sp_executesql @sql;
SET @sql=N'BACKUP CERTIFICATE pp_ErpConfigurationCertificate TO FILE=N'''
    + REPLACE(@CertificatePath,N'''',N'''''') + N''' WITH PRIVATE KEY (FILE=N'''
    + REPLACE(@PrivatePath,N'''',N'''''') + N''', ENCRYPTION BY PASSWORD=' + @secret + N');';
EXEC sys.sp_executesql @sql;
'@ @{ '@Password' = $recoveryText; '@MasterPath' = $masterBackup; '@CertificatePath' = $certificateBackup; '@PrivatePath' = $privateBackup }

    $stage = 'checking runtime-login credentials'
    $loginExists = (Invoke-Sql $adminConnection 'SELECT COUNT(*) FROM sys.server_principals WHERE name=@Login' @{ '@Login' = $RuntimeLogin } -Scalar) -eq 1
    if ($loginExists -and $null -eq $RuntimePassword) {
        throw 'RuntimeLogin already exists. Supply its current password using RuntimePassword; it will not be changed.'
    }
    if ($null -eq $RuntimePassword) {
        $runtimeText = 'Pp!9' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
    } else { $runtimeText = Get-SecretText $RuntimePassword }
    if ($runtimeText.Length -eq 0 -or $runtimeText.Length -gt 128) { throw 'RuntimePassword must contain 1 to 128 characters.' }
    if (-not $loginExists -and (Invoke-Sql $adminConnection "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR HAS_PERMS_BY_NAME(NULL,NULL,'ALTER ANY LOGIN')=1 THEN 1 ELSE 0 END" -Scalar) -ne 1) {
        throw 'Creating a dedicated runtime login requires ALTER ANY LOGIN or sysadmin. A DBA can pre-create it instead.'
    }
    $stage = 'provisioning the restricted runtime login'
    Invoke-Sql $adminConnection @'
DECLARE @sql nvarchar(max);
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name=@Login)
BEGIN
    SET @sql=N'CREATE LOGIN ' + QUOTENAME(@Login) + N' WITH PASSWORD=N'''
        + REPLACE(@Password,N'''',N'''''') + N''', CHECK_POLICY=ON, CHECK_EXPIRATION=OFF;';
    EXEC sys.sp_executesql @sql;
END;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name=@Login AND type='S')
    THROW 50104, 'Runtime login name belongs to a different principal type.', 1;
IF EXISTS (SELECT 1 FROM sys.server_role_members WHERE member_principal_id=SUSER_ID(@Login))
    OR EXISTS (SELECT 1 FROM sys.server_permissions WHERE grantee_principal_id=SUSER_ID(@Login)
               AND state IN ('G','W') AND permission_name<>N'CONNECT SQL')
    OR SUSER_SID(@Login)=(SELECT sid FROM sys.database_principals WHERE name=N'dbo')
    THROW 50104, 'Runtime login has administrative or unrelated server privileges.', 1;
IF DATABASE_PRINCIPAL_ID(@Login) IS NULL
BEGIN
    SET @sql=N'CREATE USER ' + QUOTENAME(@Login) + N' FOR LOGIN ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @sql;
END;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name=@Login AND type='S' AND sid=SUSER_SID(@Login))
    THROW 50104, 'Runtime database user maps to a different login.', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members rm JOIN sys.database_principals r ON r.principal_id=rm.role_principal_id
           WHERE rm.member_principal_id=DATABASE_PRINCIPAL_ID(@Login) AND r.name<>N'ppEvidencija_runtime')
    OR EXISTS (SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(@Login)
               AND state IN ('G','W') AND permission_name<>N'CONNECT')
    THROW 50104, 'Runtime user has privileges outside the application role.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id=DATABASE_PRINCIPAL_ID(N'ppEvidencija_runtime')
               AND member_principal_id=DATABASE_PRINCIPAL_ID(@Login))
BEGIN
    SET @sql=N'ALTER ROLE ppEvidencija_runtime ADD MEMBER ' + QUOTENAME(@Login) + N';';
    EXEC sys.sp_executesql @sql;
END;
'@ @{ '@Login' = $RuntimeLogin; '@Password' = $runtimeText }

    $builder['Integrated Security'] = $false
    $builder['User ID'] = $RuntimeLogin
    $builder['Password'] = $runtimeText
    $builder['Application Name'] = 'ppEvidencija'
    $stage = 'validating runtime access'
    $runtimeConnection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $runtimeConnection.Open()
    $allowed = Invoke-Sql $runtimeConnection @'
SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=0 AND IS_MEMBER('db_owner')=0
    AND HAS_PERMS_BY_NAME(N'dbo.pp_erp_config',N'OBJECT',N'SELECT')=0
    AND HAS_PERMS_BY_NAME(N'dbo.pp_erp_config',N'OBJECT',N'UPDATE')=0
    AND HAS_PERMS_BY_NAME(N'dbo.pp_erp_config_save',N'OBJECT',N'EXECUTE')=1
    AND HAS_PERMS_BY_NAME(N'dbo.ppaparati',N'OBJECT',N'INSERT')=1
    THEN 1 ELSE 0 END;
'@ -Scalar
    if ($allowed -ne 1) { throw 'The runtime account does not meet the required privilege boundaries.' }
    Invoke-Sql $runtimeConnection 'EXEC dbo.pp_erp_config_read;'

    Invoke-Sql $runtimeConnection 'EXEC dbo.pp_erp_config_metadata;'

    $stage = 'saving the validated local bootstrap connection'
    $settings['ConnectionStrings']['DefaultConnection'] = $builder.ConnectionString
    $temporarySettings = [IO.Path]::Combine([IO.Path]::GetDirectoryName($settingsFullPath), '.appsettings-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporarySettings, ($settings | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporarySettings -Destination $settingsFullPath -Force
    $temporarySettings = $null
    Write-Host "ERP security initialized in [$DatabaseName] on $ServerInstance."
    Write-Host "Validated runtime connection saved to $settingsFullPath. Rebuild/copy this settings file with the application."
    Write-Host "Encrypted recovery files: $masterBackup ; $certificateBackup ; $privateBackup"
    Write-Host 'Open Configuration on the login screen to test and save the ERP connection. No ERP source was seeded.'
}
catch {
    # SQL command text and connection strings can contain credentials. Do not print the exception object.
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    $sqlFailure = if ($failure -is [System.Data.SqlClient.SqlException]) { " SQL error $($failure.Number)." } else { " $($failure.GetType().Name), line $($_.InvocationInfo.ScriptLineNumber)." }
    Write-Error "Initialization stopped while $stage.$sqlFailure Local settings were not replaced unless the success message was shown. Check permissions, required secure inputs, and SQL Server recovery-directory access. Existing runtime logins require their current RuntimePassword. If provisioning partially completed, rerun with the existing login password or have a DBA reset that login."
    exit 1
}
finally {
    if ($null -ne $runtimeConnection) { $runtimeConnection.Dispose() }
    if ($null -ne $adminConnection) { $adminConnection.Dispose() }
    if ($null -ne $temporarySettings -and [IO.File]::Exists($temporarySettings)) {
        # Only the exact temporary file created next to the explicitly selected settings file is removed.
        Remove-Item -LiteralPath $temporarySettings -Force
    }
    $recoveryText = $null; $existingKeyText = $null; $runtimeText = $null
}
