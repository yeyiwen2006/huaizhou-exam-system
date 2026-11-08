#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/deployment/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "请使用尚不存在的输出目录，避免覆盖已填写的部署配置。目录为 $destination"
}
New-Item -ItemType Directory -Path $destination | Out-Null

function Copy-ReleaseFile([string]$RelativePath, [string]$Package, [string]$TargetPath = $RelativePath) {
    $target = Join-Path $destination "$Package/$TargetPath"
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $RelativePath) -Destination $target
}

$programs = [ordered]@{
    'Exam.Server' = 'server/Exam.Server'
    'Exam.Management' = 'server/Exam.Management'
    'Exam.Admin' = 'admin'
    'Exam.Cli' = 'admin/cli'
    'Exam.Student' = 'student'
}
foreach ($program in $programs.GetEnumerator()) {
    Write-Host "正在生成 $($program.Key) 的 Windows x64 部署文件…"
    & dotnet publish (Join-Path $repository "src/$($program.Key)/$($program.Key).csproj") `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:RestoreLockedMode=true --output (Join-Path $destination $program.Value)
    if ($LASTEXITCODE -ne 0) { throw "$($program.Key) 发布失败，已生成的文件保留在 $destination" }
}

# Keep an explicit reference tree for both server operators and the admin-computer CLI.
# Do not recurse through docs or configuration directories: they can contain local reports and connections.
$referenceFiles = @(
    'docs/cli-usage.md', 'docs/gui-cli-coverage.md', 'docs/service-management.md',
    'docs/high-availability-management.md', 'docs/backup-and-failover-runbook.md',
    'docs/acceptance-criteria.md', 'docs/operations/automatic-backup-retention.md',
    'docs/operations/capacity-observability.md', 'docs/operations/postgresql-maintenance.md',
    'docs/operations/ha-audit-repair-20261018.md',
    'deploy/server/README.md', 'deploy/server/Install-WindowsServices.ps1', 'deploy/server/Publish-Server.ps1',
    'deploy/server/appsettings.business.linux.example.json',
    'deploy/server/appsettings.management.linux.example.json',
    'deploy/server/service-configuration.linux.example.json', 'deploy/server/install-systemd.sh',
    'deploy/management/Export-AdminConnection.ps1', 'deploy/server-capacity.example.json',
    'deploy/student-failover.example.json', 'deploy/student/README.md',
    'deploy/student/server.example.json', 'deploy/student/server.public.example.json',
    'deploy/student/server.public.single.example.json',
    'samples/service-management/appsettings.business.example.json',
    'samples/service-management/appsettings.management.example.json',
    'samples/service-management/appsettings.high-availability.example.json',
    'samples/service-management/appsettings.single-node.example.json',
    'samples/service-management/service-configuration.example.json',
    'samples/service-management/ha-takeover-request.example.json', 'eng/maintenance/README.md',
    'deploy/postgresql/README.md', 'deploy/postgresql/check-deployment.example.json',
    'deploy/postgresql/pg_hba.conf.fragment', 'deploy/postgresql/primary/postgresql.conf.fragment',
    'deploy/postgresql/standby/postgresql.conf.fragment',
    'deploy/postgresql/scripts/Initialize-Standby.ps1',
    'deploy/postgresql/scripts/Invoke-ExamMaintenance.ps1',
    'deploy/postgresql/scripts/Invoke-WalArchive.ps1',
    'deploy/postgresql/scripts/Invoke-WalArchiveCommand.ps1',
    'deploy/postgresql/scripts/New-PostgresqlDeployment.ps1',
    'deploy/postgresql/scripts/Postgresql.Operations.psm1',
    'deploy/postgresql/scripts/Restore-WalArchive.ps1',
    'deploy/postgresql/scripts/Test-ExamDeployment.ps1',
    'deploy/postgresql/scripts/Test-ReplicationReadiness.ps1',
    'deploy/postgresql/scripts/Test-StorageCapacity.ps1'
)
foreach ($relative in $referenceFiles) {
    Copy-ReleaseFile $relative 'server'
    Copy-ReleaseFile $relative 'admin'
}
Copy-ReleaseFile 'deploy/server/部署说明.md' 'server' '部署说明.md'
Copy-ReleaseFile 'deploy/admin/README.md' 'admin' '部署说明.md'
$cliGuide = "# 考务 CLI`r`n`r`n运行本目录中的 ``Exam.Cli.exe help`` 查看命令。连接、登录、业务操作与部署排障步骤见[完整使用说明](../docs/cli-usage.md)。`r`n"
[IO.File]::WriteAllText((Join-Path $destination 'admin/cli/使用说明.md'), $cliGuide, [Text.UTF8Encoding]::new($false))
Copy-ReleaseFile 'deploy/student/README.md' 'student' '部署说明.md'
Copy-ReleaseFile 'deploy/student/server.example.json' 'student' 'server.example.json'
Copy-ReleaseFile 'deploy/student/server.public.example.json' 'student' 'server.public.example.json'
Copy-ReleaseFile 'deploy/student/server.public.single.example.json' 'student' 'server.public.single.example.json'
foreach ($client in @('admin', 'student')) {
    $executable = if ($client -eq 'admin') { 'Exam.Admin.exe' } else { 'Exam.Student.exe' }
    $title = if ($client -eq 'admin') { '启动考务端.cmd' } else { '启动学生端.cmd' }
    $content = "$([char]64)echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"%~dp0$executable`"`r`n"
    [IO.File]::WriteAllText((Join-Path $destination "$client/$title"), $content, [Text.Encoding]::ASCII)
}
Copy-Item -LiteralPath (Join-Path $repository '部署指南.md') -Destination (Join-Path $destination '部署指南.md')
foreach ($package in @('server', 'admin', 'student')) {
    $zip = Join-Path $destination ("Exam.$package-win-x64.zip")
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $destination $package), $zip)
}
Write-Host "部署包已生成，目录为 $destination"
Write-Host 'server 放到服务器，admin 放到考务电脑，student 放到学生电脑。各目录中的“部署说明.md”列出了配置和启动入口。'
