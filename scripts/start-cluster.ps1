# Launches a 3-node DocumentForge cluster locally.
# Each node: own port, own data directory, own log window.
# All nodes share the same cluster.json config file.
#
# Usage (from repo root):
#     .\scripts\start-cluster.ps1
#
# Stop with:
#     .\scripts\stop-cluster.ps1

$ErrorActionPreference = 'Stop'

$root = (Get-Location).Path
$cluster = Join-Path $root 'scripts/sample-cluster'

Write-Host ""
Write-Host "=== Starting DocumentForge local cluster ===" -ForegroundColor Cyan
Write-Host "  Shard A:  http://localhost:5001   data: $cluster/data/shard-a"
Write-Host "  Shard B:  http://localhost:5002   data: $cluster/data/shard-b"
Write-Host "  Shard C:  http://localhost:5003   data: $cluster/data/shard-c"
Write-Host ""

# Start each node in its own window so logs are visible
Start-Process pwsh -ArgumentList @(
    '-NoExit', '-Command',
    "dotnet run --project `"$root/src/DocumentForge.Cli`" --configuration Release -- serve --config `"$cluster/node-a.json`""
) -WorkingDirectory $root

Start-Process pwsh -ArgumentList @(
    '-NoExit', '-Command',
    "dotnet run --project `"$root/src/DocumentForge.Cli`" --configuration Release -- serve --config `"$cluster/node-b.json`""
) -WorkingDirectory $root

Start-Process pwsh -ArgumentList @(
    '-NoExit', '-Command',
    "dotnet run --project `"$root/src/DocumentForge.Cli`" --configuration Release -- serve --config `"$cluster/node-c.json`""
) -WorkingDirectory $root

Write-Host ""
Write-Host "Three windows opened, one per shard. Wait ~5 seconds for them to start."
Write-Host ""
Write-Host "Then check health:" -ForegroundColor Yellow
Write-Host "  dfdb health $cluster/cluster.json"
Write-Host ""
Write-Host "Or point the admin UI at shard A:" -ForegroundColor Yellow
Write-Host "  cd admin-ui"
Write-Host '  $env:NEXT_PUBLIC_DFDB_URL = "http://localhost:5001"'
Write-Host "  npm run dev"
Write-Host ""
