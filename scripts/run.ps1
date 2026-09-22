<#
.SYNOPSIS
    Standard launch script for McpSqlServer.
    Enforces RULE.md: Requires 100% passing unit tests before starting the application.
#>

$ErrorActionPreference = "Stop"

# Resolve repository root directory via relative path
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " [RUN GATE] Executing Unit Test Suite" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Run Unit Tests
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[FATAL] Unit tests FAILED! Aborting application startup." -ForegroundColor Red
    exit 1
}

Write-Host "[SUCCESS] All unit tests PASSED!" -ForegroundColor Green
Write-Host "----------------------------------------" -ForegroundColor Gray

# 2. Start Application
Write-Host "Starting McpSqlServer with arguments: $args" -ForegroundColor Cyan
dotnet run --project ./src/McpSqlServer/McpSqlServer.csproj -- $args
