# DocumentForge REST benchmark.
# Targets either a local `docker run` (default) or your Render URL.
# Measures 5 workload types for a few seconds each; reports QPS + latency percentiles.
#
# Usage (default — local Docker at localhost:5000):
#     .\scripts\rest-bench.ps1
#
# Against Render:
#     .\scripts\rest-bench.ps1 -Url "https://documentforge.onrender.com" -ApiKey "test-api-key"
#
# Longer run:
#     .\scripts\rest-bench.ps1 -Seconds 10

param(
    [string]$Url = "http://localhost:5000",
    [string]$ApiKey = "",
    [int]$Seconds = 3
)

$H = @{ 'Content-Type' = 'application/json' }
if ($ApiKey) { $H['Authorization'] = "Bearer $ApiKey" }

function Invoke-Phase {
    param(
        [string]$Name,
        [scriptblock]$Call,
        [int]$Duration = $Seconds
    )
    # 200ms warmup so JIT + CLR + connection-pool effects don't pollute the first phase
    try { & $Call | Out-Null } catch { }
    Start-Sleep -Milliseconds 200

    $latencies = [System.Collections.Generic.List[double]]::new()
    $errors = 0
    $end = (Get-Date).AddSeconds($Duration)
    while ((Get-Date) -lt $end) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try   { & $Call | Out-Null;  $sw.Stop(); $latencies.Add($sw.Elapsed.TotalMilliseconds) }
        catch { $sw.Stop(); $errors++ }
    }
    $count = $latencies.Count
    if ($count -eq 0) { Write-Host "  $Name - no successful requests" -ForegroundColor Red; return }

    $sorted = $latencies | Sort-Object
    $avg = [math]::Round(($latencies | Measure-Object -Average).Average, 2)
    $p50 = [math]::Round($sorted[[int]($count * 0.50)], 2)
    $p95 = [math]::Round($sorted[[int][math]::Min($count-1, $count * 0.95)], 2)
    $p99 = [math]::Round($sorted[[int][math]::Min($count-1, $count * 0.99)], 2)
    $qps = [math]::Round($count / $Duration, 1)

    Write-Host ""
    Write-Host "  $Name" -ForegroundColor Cyan
    Write-Host ("    {0,6} requests  |  {1,6} QPS  |  {2} errors" -f $count, $qps, $errors)
    Write-Host ("    avg {0}ms   p50 {1}ms   p95 {2}ms   p99 {3}ms" -f $avg, $p50, $p95, $p99)
}

Write-Host ""
Write-Host "=== DocumentForge REST benchmark ===" -ForegroundColor Yellow
Write-Host "  target : $Url"
Write-Host "  auth   : $( if ($ApiKey) { 'bearer token' } else { 'none' } )"
Write-Host "  phases : 5 x ${Seconds}s"
Write-Host ""

# Health check + discover whether data exists
$health = Invoke-RestMethod -Headers $H "$Url/health"
Write-Host "  node   : $($health.node)  (uptime $($health.uptimeSeconds)s)" -ForegroundColor DarkGray

$stats = Invoke-RestMethod -Headers $H "$Url/stats"
$hasOrders = $stats.collections | Where-Object { $_.name -eq 'orders' }
if (-not $hasOrders) {
    Write-Host "  seeding 500 orders + 100 flights + 5 indexes..." -ForegroundColor DarkGray
    Invoke-RestMethod -Method Post -Headers $H "$Url/seed" -Body '{"orders":500}' | Out-Null
}

# Get a real PNR and a real lastName to make the workload realistic
$r = Invoke-RestMethod -Method Post -Headers $H "$Url/query" -Body '{"sql":"SELECT pnr, passenger.lastName AS ln FROM orders LIMIT 1"}'
$pnr = $r.documents[0].pnr
$lastName = $r.documents[0].ln
Write-Host "  sample : pnr=$pnr  lastName=$lastName" -ForegroundColor DarkGray

$sqlPoint  = "{`"sql`":`"SELECT * FROM orders WHERE pnr = '$pnr'`"}"
$sqlByName = "{`"sql`":`"SELECT * FROM orders WHERE passenger.lastName = '$lastName' LIMIT 10`"}"
$sqlAgg    = '{"sql":"SELECT status, COUNT(*) FROM orders GROUP BY status"}'

Invoke-Phase "GET /health                         (network RTT baseline)" `
    { Invoke-RestMethod -Headers $H "$Url/health" }

Invoke-Phase "Point lookup by PNR   (SQL, indexed)" `
    { Invoke-RestMethod -Method Post -Headers $H "$Url/query" -Body $sqlPoint }

Invoke-Phase "Point lookup by PNR   (NEW /by route)" `
    { Invoke-RestMethod -Headers $H "$Url/collections/orders/by/pnr/$pnr" }

Invoke-Phase "Last-name query   (SQL, indexed nested path, LIMIT 10)" `
    { Invoke-RestMethod -Method Post -Headers $H "$Url/query" -Body $sqlByName }

Invoke-Phase "Status breakdown   (COLLECTION_SCAN + AGGREGATE)" `
    { Invoke-RestMethod -Method Post -Headers $H "$Url/query" -Body $sqlAgg }

Write-Host ""
Write-Host "Notes:" -ForegroundColor DarkGray
Write-Host "  - Latency p50 ~= network RTT for indexed queries (engine is usually sub-ms)."
Write-Host "  - QPS is bounded by sequential RTT; real apps make parallel requests."
Write-Host "  - /health is unauthenticated; other routes use the bearer token if provided."
Write-Host ""
