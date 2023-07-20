# Builds self-contained single-file dfdb binaries for Windows, Linux and macOS.
# Output goes to ./dist/<rid>/dfdb[.exe]
#
# Usage:
#     .\scripts\publish-dfdb.ps1
#     .\scripts\publish-dfdb.ps1 -Rids "win-x64,linux-x64"   # subset

param(
    [string]$Rids = "win-x64,linux-x64,osx-x64,osx-arm64,linux-arm64"
)

$ErrorActionPreference = 'Stop'

$root = (Get-Location).Path
$project = "$root/src/DocumentForge.Cli"
$dist = "$root/dist"

foreach ($rid in $Rids.Split(',')) {
    $rid = $rid.Trim()
    Write-Host ""
    Write-Host "=== Publishing dfdb for $rid ===" -ForegroundColor Cyan
    $out = "$dist/$rid"

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        --output $out

    $binary = if ($rid.StartsWith('win-')) { "dfdb.exe" } else { "dfdb" }
    $fullPath = Join-Path $out $binary
    if (Test-Path $fullPath) {
        $sizeMb = [math]::Round((Get-Item $fullPath).Length / 1MB, 1)
        Write-Host "  → $fullPath ($sizeMb MB)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "All done. Drop the binary on any machine and run it:"
Write-Host "  dfdb serve --port 5000 --data-dir ./data" -ForegroundColor Yellow
Write-Host ""
