# Builds the app into .\app\ for your own use (needs the .NET 10 SDK; the app needs the .NET 10 Desktop Runtime).
# If a .\brand\ folder exists, that edition is built instead (see BrandDir in src\RedDrop.csproj).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'app'
$brand = Join-Path $PSScriptRoot 'brand'

if (-not (Test-Path (Join-Path $src 'icon.ico'))) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }

# The exe can't be overwritten while it's running.
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out\*" }
if ($running) {
    Write-Host "Closing $($running.Name -join ', ')..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# bin/obj are shared by both editions, so start clean.
Remove-Item (Join-Path $src 'bin'), (Join-Path $src 'obj') -Recurse -Force -ErrorAction SilentlyContinue
$extra = @()
if (Test-Path (Join-Path $brand 'Brand.props')) { $extra += "-p:BrandDir=$brand" }
dotnet publish $src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out @extra
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built into $out"
