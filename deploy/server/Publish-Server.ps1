param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($component in @('Exam.Server', 'Exam.Management')) {
    $output = Join-Path $destination $component
    if (Test-Path -LiteralPath (Join-Path $output 'appsettings.json')) {
        throw "Publish to a new directory to preserve the existing deployment configuration: $output"
    }
    & dotnet publish (Join-Path $repository "src/$component/$component.csproj") `
        --configuration Release --runtime $Runtime --self-contained true --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
}
Write-Output "Published $Runtime server programs to $destination. Configure both appsettings.json files before installing services."
