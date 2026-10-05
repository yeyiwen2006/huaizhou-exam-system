param(
    [Parameter(Mandatory = $true)][string]$CliPath,
    [Parameter(Mandatory = $true)][ValidateSet('status', 'ha-status', 'checks', 'backup', 'restore', 'rebuild', 'operation')][string]$Operation,
    [Parameter(Mandatory = $true)][string]$ServerId,
    [Parameter(Mandatory = $true)][string]$NodeId,
    [Parameter(Mandatory = $true)][string]$ManagementUrl,
    [string]$InputPath,
    [string]$RequestId
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$cli = Assert-ControlledPath $CliPath
if (-not [IO.File]::Exists($cli)) { throw 'The published Exam.Cli executable is missing.' }
$uri = [Uri]$ManagementUrl
if ($uri.Scheme -ne 'https' -or -not $uri.IsLoopback -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'Use the authenticated local HTTPS management endpoint.'
}
$arguments = @('management', $Operation, '--management-url', $ManagementUrl, '--server-id', $ServerId, '--node-id', $NodeId)
if ($Operation -in @('checks', 'backup', 'restore', 'rebuild')) {
    if ($RequestId -or -not $InputPath) { throw 'Mutating/check operations require their original JSON request file, not a new request identifier.' }
    $input = Assert-ControlledPath $InputPath
    if (-not [IO.File]::Exists($input)) { throw 'Request JSON must be an existing file.' }
    $arguments += @('--input', $input)
} elseif ($Operation -eq 'operation') {
    if (-not $RequestId -or $InputPath) { throw 'Operation lookup requires only the original request identifier.' }
    $arguments += @('--request-id', $RequestId)
} elseif ($InputPath -or $RequestId) { throw 'Status operations do not accept request input.' }
# No shell-built command, no credentials in arguments, no duplicate backup/promotion rules.
& $cli @arguments
exit $LASTEXITCODE
