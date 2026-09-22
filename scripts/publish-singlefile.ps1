<#
.SYNOPSIS
    Packages McpSqlServer as a standalone single-file executable.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$PublishDir = "./publish"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Packaging McpSqlServer Single-File Executable" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Run tests before packaging
Write-Host "1. Verifying Unit Tests..." -ForegroundColor Yellow
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Unit tests failed. Aborting packaging!" -ForegroundColor Red
    exit 1
}

# 2. Publish single-file
Write-Host "`n2. Publishing single-file binary to relative path: $PublishDir..." -ForegroundColor Yellow
dotnet publish ./src/McpSqlServer/McpSqlServer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $PublishDir

Write-Host "`n[COMPLETE] Executable successfully published to: $PublishDir/McpSqlServer.exe" -ForegroundColor Green
