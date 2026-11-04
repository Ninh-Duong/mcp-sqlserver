<#
.SYNOPSIS
    The mcp-hub `prepare` step: publish publish/McpSqlServer.exe only when it is missing or older than src/.

.DESCRIPTION
    Idempotent and quick when the build is current. Writes only to stderr (never stdout) and prints no
    configuration values. Exit 0: the build is ready. Exit 1: publishing failed; an existing build is kept.
    The same freshness rule is checked at `serve` start by BuildFreshness.cs.
#>
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "publish/McpSqlServer.exe"

function Write-Status([string]$message) { [Console]::Error.WriteLine("[prepare] $message") }

function Test-BuildCurrent {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $false }
    # The dll is rewritten on every rebuild; publish may skip copying an unchanged apphost exe.
    $dll = Join-Path $root "publish/McpSqlServer.dll"
    $stamp = if (Test-Path -LiteralPath $dll -PathType Leaf) { $dll } else { $exe }
    $built = (Get-Item -LiteralPath $stamp).LastWriteTimeUtc
    $newer = Get-ChildItem -LiteralPath (Join-Path $root "src") -Recurse -File -Include *.cs, *.csproj, *.props, *.targets |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $built } |
        Select-Object -First 1
    return $null -eq $newer
}

function Test-ExeInUse {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $false }
    try {
        [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Dispose()
        return $false
    } catch [IO.IOException] {
        return $true
    }
}

if (Test-BuildCurrent) {
    Write-Status "publish/McpSqlServer.exe is up to date."
    exit 0
}
if (Test-ExeInUse) {
    Write-Status "publish/McpSqlServer.exe is running (an MCP client started it outside the Hub?). Close it and run again; the existing build is kept."
    exit 1
}

Write-Status "Publishing publish/McpSqlServer.exe ..."
& dotnet publish (Join-Path $root "src/McpSqlServer") -c Release -o (Join-Path $root "publish") --nologo -v q 2>&1 |
    ForEach-Object { [Console]::Error.WriteLine($_) }
if ($LASTEXITCODE -ne 0) {
    Write-Status "dotnet publish failed (exit $LASTEXITCODE)."
    exit 1
}
Write-Status "publish/McpSqlServer.exe is ready."
exit 0
