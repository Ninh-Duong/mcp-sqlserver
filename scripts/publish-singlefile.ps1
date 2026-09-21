<#
.SYNOPSIS
    Kịch bản đóng gói McpSqlServer thành Single-File Executable độc lập.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$PublishDir = "./publish"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Đóng gói McpSqlServer Single-File Executable" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Chạy test trước khi đóng gói
Write-Host "1. Kiểm tra Unit Tests..." -ForegroundColor Yellow
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Unit tests thất bại. Hủy đóng gói!" -ForegroundColor Red
    exit 1
}

# 2. Publish single-file
Write-Host "`n2. Xuất bản Single-File vào thư mục tương đối $PublishDir..." -ForegroundColor Yellow
dotnet publish ./src/McpSqlServer/McpSqlServer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $PublishDir

Write-Host "`n[HOÀN TẤT] File thực thi đã được xuất bản tại: $PublishDir/McpSqlServer.exe" -ForegroundColor Green
