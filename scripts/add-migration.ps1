<#
.SYNOPSIS
  为 Plinth.App 生成 EF Core 迁移。
.EXAMPLE
  pwsh scripts/add-migration.ps1 InitialCreate
.EXAMPLE
  pwsh scripts/add-migration.ps1 Demo_AddAuditFields
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern("^[A-Za-z][A-Za-z0-9_]*$")]
    [string]$Name
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "没有找到 dotnet。请先安装 .NET SDK 10.0.403。"
}

$project = "src/Plinth.App/Plinth.App.csproj"
$migrationDirectory = "src/Plinth.App/Migrations"

Write-Host "生成迁移:$Name"
dotnet ef migrations add $Name --project $project --output-dir Migrations
if ($LASTEXITCODE -ne 0) {
    throw "生成迁移失败。请确认 dotnet-ef 已安装并且包已还原。"
}

Write-Host "迁移已生成:$migrationDirectory"
Write-Host "请检查迁移内容,再提交迁移文件和 ModelSnapshot。"
