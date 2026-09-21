<#
.SYNOPSIS
    Kịch bản khởi chạy chuẩn cho McpSqlServer.
    Tuân thủ RULE.md: Bắt buộc vượt qua 100% unit tests trước khi khởi động ứng dụng.
#>

$ErrorActionPreference = "Stop"

# Xác định thư mục gốc của repository bằng đường dẫn tương đối
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " [RUN GATE] Đang chạy toàn bộ Unit Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Chạy Unit Tests
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[FATAL] Unit tests THẤT BẠI! Dừng khởi chạy repo." -ForegroundColor Red
    exit 1
}

Write-Host "[SUCCESS] Toàn bộ Unit Tests đã PASS!" -ForegroundColor Green
Write-Host "----------------------------------------" -ForegroundColor Gray

# 2. Khởi chạy ứng dụng
Write-Host "Khởi động McpSqlServer với tham số: $args" -ForegroundColor Cyan
dotnet run --project ./src/McpSqlServer/McpSqlServer.csproj -- $args
