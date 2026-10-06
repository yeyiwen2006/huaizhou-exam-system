param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$PrimaryAddress,
    [Parameter(Mandatory = $true)][string]$StandbyAddress,
    [ValidateRange(1, 65535)][int]$PrimaryPort = 5432,
    [ValidateRange(1, 65535)][int]$StandbyPort = 5432,
    [ValidatePattern('^[a-z_][a-z0-9_]*$')][string]$ReplicationUser = 'exam_replication',
    [Parameter(Mandatory = $true)][string]$DeploymentRoot,
    [Parameter(Mandatory = $true)][string]$ArchiveRoot,
    [Parameter(Mandatory = $true)][string]$CaCertificatePath,
    [ValidateRange(1, 2147483647)][int]$MaxSlotWalKeepSizeMB = 8192,
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_.-]*$')][string]$StandbyApplicationName = 'exam_standby',
    [ValidatePattern('^[a-z0-9_]+$')][string]$ReplicationSlot = 'exam_standby'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$primary = [Net.IPAddress]::Parse($PrimaryAddress)
$standby = [Net.IPAddress]::Parse($StandbyAddress)
if ($primary.Equals($standby)) { throw 'Two distinct node IP addresses are required.' }
$output = Assert-ControlledPath $OutputDirectory -AllowMissing
if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) { throw 'Output must be a new directory; existing configurations are never overwritten.' }
$parent = Assert-ControlledPath ([IO.Path]::GetDirectoryName($output))
if (-not [IO.Directory]::Exists($parent)) { throw 'Output parent directory must exist.' }
$deploy = Assert-ControlledPath $DeploymentRoot
$archive = Assert-ControlledPath $ArchiveRoot
$ca = Assert-ControlledPath $CaCertificatePath
if (-not [IO.Directory]::Exists($deploy) -or -not [IO.Directory]::Exists($archive) -or -not [IO.File]::Exists($ca)) {
    throw 'Deployment/archive directories and a public CA certificate file must exist.'
}
if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($ca)) -match '-----BEGIN [A-Z ]*PRIVATE KEY-----') {
    throw 'The CA file must not contain any private key material.'
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($ca)
try {
    if ($certificate.HasPrivateKey -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
        $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) { throw 'The CA file must contain a currently valid public certificate only.' }
    $basicConstraints = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($basicConstraints.Count -ne 1) { throw 'The public certificate must be an explicit certificate authority.' }
    $constraints = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($basicConstraints[0], $basicConstraints[0].Critical)
    if (-not $constraints.CertificateAuthority) { throw 'The public certificate must be a certificate authority, not a leaf.' }
    # Normalize a public DER or PEM certificate to a PEM root usable by libpq/OpenSSL.
    $caPem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($certificate.RawData, [Base64FormattingOptions]::InsertLineBreaks) + "`n-----END CERTIFICATE-----`n"
} finally { $certificate.Dispose() }
$generatedCa = Join-Path $output 'ca.crt'
$primaryText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../primary/postgresql.conf.fragment'))
$standbyText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../standby/postgresql.conf.fragment'))
$replacements = @{ '{{PRIMARY_ADDRESS}}' = $primary.ToString(); '{{REPLICATION_USER}}' = $ReplicationUser;
    '{{DEPLOY_ROOT}}' = $deploy.Replace('\', '/'); '{{ARCHIVE_ROOT}}' = $archive.Replace('\', '/');
    '{{CA_CERTIFICATE_PATH}}' = $generatedCa.Replace('\', '/');
    '{{STANDBY_APPLICATION_NAME}}' = $StandbyApplicationName; '{{REPLICATION_SLOT}}' = $ReplicationSlot }
foreach ($key in $replacements.Keys) {
    $primaryText = $primaryText.Replace($key, $replacements[$key]); $standbyText = $standbyText.Replace($key, $replacements[$key])
}
$primaryText = "port = $PrimaryPort`n" + $primaryText.Replace("max_slot_wal_keep_size = '8192MB'", "max_slot_wal_keep_size = '$($MaxSlotWalKeepSizeMB)MB'")
$standbyText = "listen_addresses = '$($standby.ToString())'`nport = $StandbyPort`n" + $standbyText.Replace('port=5432', "port=$PrimaryPort")
$bits = if ($standby.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
$files = [ordered]@{ 'primary.conf.fragment' = $primaryText; 'standby.conf.fragment' = $standbyText; 'ca.crt' = $caPem;
    'pg_hba.conf.fragment' = "hostssl replication $ReplicationUser $standby/$bits scram-sha-256`n" }
if (($files.Values -join "`n") -match '\{\{') { throw 'Unresolved template values prevent generation.' }
# PostgreSQL on Windows reads native filenames/commands in the active Windows code page,
# not UTF-8 unconditionally. Encode all fragments before creating output and reject lossy paths.
if ($PSVersionTable.PSEdition -eq 'Core') {
    [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
}
$nativeEncoding = [Text.Encoding]::GetEncoding([Text.Encoding]::GetEncoding(0).CodePage,
    [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
$fileBytes = [ordered]@{}
foreach ($entry in $files.GetEnumerator()) {
    $encoding = if ($entry.Key.EndsWith('.fragment')) { $nativeEncoding } else { [Text.UTF8Encoding]::new($false) }
    $fileBytes[$entry.Key] = $encoding.GetBytes($entry.Value)
}
# CreateDirectory is not exclusive. An exclusive sibling claim prevents competing generators
# using this tool; verify emptiness again, and never overwrite even on unexpected races.
$claim = [IO.File]::Open($output + '.generation.lock', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) { throw 'Output appeared during generation; it is preserved.' }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    foreach ($entry in $files.GetEnumerator()) {
        $stream = [IO.File]::Open((Join-Path $output $entry.Key), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = $fileBytes[$entry.Key]; $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    [ordered]@{ outputDirectory = $output; applied = $false; primary = $primary.ToString(); standby = $standby.ToString();
        maxSlotWalKeepSizeMB = $MaxSlotWalKeepSizeMB; files = @($files.Keys) } | ConvertTo-Json
} finally { $claim.Dispose(); [IO.File]::Delete($output + '.generation.lock') }
