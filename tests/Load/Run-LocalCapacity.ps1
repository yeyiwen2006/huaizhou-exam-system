param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(1, 86400)][int]$SteadySeconds = 120,
    [ValidateSet('mixed', 'separate')][string]$RunMode = 'mixed',
    [ValidateRange(1024, 65535)][int]$Port = 5089
)
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'run_local_capacity.py'
& python $entry --fixture $FixtureDirectory --report $ReportDirectory --duration-seconds $SteadySeconds --port $Port --run-mode $RunMode
if ($LASTEXITCODE -ne 0) { throw 'Shared capacity coordinator failed. Inspect its sanitized report.' }
