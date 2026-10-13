[CmdletBinding(DefaultParameterSetName = 'Account')]
param(
    [Parameter(Mandatory = $true)][string]$DeploymentDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Account')][PSCredential]$ServiceCredential,
    [Parameter(Mandatory = $true, ParameterSetName = 'System')][switch]$UseLocalSystem
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this installer on Windows.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer in an elevated PowerShell window.' }
$root = [IO.Path]::GetFullPath($DeploymentDirectory)
$serverDirectory = Join-Path $root 'Exam.Server'
$managementDirectory = Join-Path $root 'Exam.Management'
$server = [IO.File]::ReadAllText((Join-Path $serverDirectory 'appsettings.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$management = [IO.File]::ReadAllText((Join-Path $managementDirectory 'appsettings.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$businessName = [string]$server.ExamServer.WindowsServiceName
$managementName = [string]$management.Management.ServiceName
if ($businessName -ne [string]$management.Management.BusinessServiceName -or $businessName -eq $managementName) {
    throw 'The business service names must match, and management must use a separate service name.'
}
foreach ($name in @($businessName, $managementName)) {
    if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$') { throw 'Invalid configured service name.' }
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw "Service already exists; it was not changed: $name" }
}
foreach ($path in @((Join-Path $serverDirectory 'Exam.Server.exe'), (Join-Path $managementDirectory 'Exam.Management.exe'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Published executable is missing: $path" }
}
$account = @{}
if ($PSCmdlet.ParameterSetName -eq 'Account') { $account.Credential = $ServiceCredential }
New-Service -Name $businessName -BinaryPathName ('"' + (Join-Path $serverDirectory 'Exam.Server.exe') + '"') `
    -DisplayName 'Huaizhou Exam Business' -StartupType Manual @account | Out-Null
New-Service -Name $managementName -BinaryPathName ('"' + (Join-Path $managementDirectory 'Exam.Management.exe') + '"') `
    -DisplayName 'Huaizhou Exam Management' -StartupType Automatic @account | Out-Null

# Permit the selected management account to query/start/stop only the configured business service.
# Other existing service permissions are preserved.
if ($PSCmdlet.ParameterSetName -eq 'Account') {
    $sid = ([Security.Principal.NTAccount]::new($ServiceCredential.UserName)).Translate([Security.Principal.SecurityIdentifier])
    $sc = Join-Path $env:SystemRoot 'System32/sc.exe'
    $lines = & $sc sdshow $businessName
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the business service permissions.' }
    $sddl = ($lines | Where-Object { $_ -match '^D:' } | Select-Object -First 1).Trim()
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
    $ace = [Security.AccessControl.CommonAce]::new([Security.AccessControl.AceFlags]::None,
        [Security.AccessControl.AceQualifier]::AccessAllowed, 0x34, $sid, $false, $null)
    $descriptor.DiscretionaryAcl.InsertAce(0, $ace)
    & $sc sdset $businessName $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::All) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to grant business service control to the management account.' }
}
Write-Output "Installed $businessName (manual) and $managementName (automatic), without starting them."
Write-Output 'The service account needs Log on as a service and access to configured certificates, database, resources, state and log paths.'
