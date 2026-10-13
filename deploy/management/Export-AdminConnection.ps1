param(
    [Parameter(Mandatory = $true)][string]$ManagementConfigurationPath,
    [Parameter(Mandatory = $true)][string]$AdminDirectory,
    [Parameter(Mandatory = $true)][string]$HostName
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$configurationPath = [IO.Path]::GetFullPath($ManagementConfigurationPath)
$outputDirectory = [IO.Path]::GetFullPath($AdminDirectory)
if (-not [IO.Directory]::Exists($outputDirectory)) { throw 'AdminDirectory must be an existing deployment directory.' }
$outputPath = Join-Path $outputDirectory 'management-connection.json'
if ($configurationPath -eq $outputPath) { throw 'The connection output must not overwrite the management configuration.' }
for ($node = [IO.DirectoryInfo]::new($outputDirectory); $null -ne $node; $node = $node.Parent) {
    if (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The output directory must not traverse links.' }
}
if ([IO.File]::Exists($outputPath) -and (([IO.File]::GetAttributes($outputPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'The connection output must not be a link.'
}
try {
    $configuration = [IO.File]::ReadAllText($configurationPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
} catch { throw 'Cannot read the management configuration JSON.' }
$management = $configuration.Management
if ([Uri]::CheckHostName($HostName.Trim('[', ']')) -eq [UriHostNameType]::Unknown) {
    throw 'HostName must be the server DNS name or IP address covered by its HTTPS certificate, without a scheme, path or port.'
}
$port = 0
$serverId = [string]$management.ServerId
$nodeId = [string]$management.NodeId
if (-not [int]::TryParse([string]$management.HttpsPort, [ref]$port) -or $port -lt 1 -or $port -gt 65535 -or
    [string]::IsNullOrWhiteSpace($serverId) -or $serverId.Length -gt 200 -or
    [string]::IsNullOrWhiteSpace($nodeId) -or $nodeId.Length -gt 200) {
    throw 'Management.HttpsPort, Management.ServerId and Management.NodeId must match the deployed management service.'
}
# Export only routing information. Do not copy database passwords, certificates or operator credentials.
$address = [UriBuilder]::new('https', $HostName.Trim('[', ']'), $port)
if ($port -eq 443) { $address.Port = -1 }
$connection = [ordered]@{ address = $address.Uri.AbsoluteUri; serverId = $serverId.Trim(); nodeId = $nodeId.Trim() }
$temporary = Join-Path $outputDirectory ('.management-connection-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, ($connection | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $outputPath -Force
} finally {
    if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary }
}
Write-Output "Management connection configuration saved: $outputPath"
