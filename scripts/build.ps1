<#
.SYNOPSIS
    Build and verification script for McpSqlServer.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Restoring and Building McpSqlServer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Restore
Write-Host "1. Restoring packages (dotnet restore)..." -ForegroundColor Yellow
dotnet restore ./McpSqlServer.slnx

# 2. Build Release
Write-Host "`n2. Compiling solution (Release mode)..." -ForegroundColor Yellow
dotnet build ./McpSqlServer.slnx -c Release --no-restore

# 3. Test
Write-Host "`n3. Executing Unit Test Suite..." -ForegroundColor Yellow
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj -c Release --no-build

Write-Host "`n[COMPLETE] Build and tests completed successfully!" -ForegroundColor Green
