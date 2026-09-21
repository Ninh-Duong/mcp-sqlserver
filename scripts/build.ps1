<#
.SYNOPSIS
    Kịch bản Build & Verify cho McpSqlServer.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Đang khôi phục và Build McpSqlServer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Restore
Write-Host "1. Khôi phục packages (dotnet restore)..." -ForegroundColor Yellow
dotnet restore ./McpSqlServer.slnx

# 2. Build Release
Write-Host "`n2. Biên dịch dự án (Release mode)..." -ForegroundColor Yellow
dotnet build ./McpSqlServer.slnx -c Release --no-restore

# 3. Test
Write-Host "`n3. Thực thi toàn bộ Unit Tests..." -ForegroundColor Yellow
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj -c Release --no-build

Write-Host "`n[HOÀN TẤT] Build & Test thành công!" -ForegroundColor Green
