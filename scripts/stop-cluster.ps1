# Stops all locally-running dfdb nodes.
# Kills any process matching "dfdb" or "DocumentForge.Cli" (dev mode via dotnet run).
# Use with caution if you have other dotnet projects running.

Write-Host "Stopping DocumentForge nodes..." -ForegroundColor Yellow

Get-Process | Where-Object { $_.ProcessName -eq 'dfdb' -or $_.ProcessName -eq 'DocumentForge.Cli' } | ForEach-Object {
    Write-Host "  Killing PID $($_.Id) ($($_.ProcessName))"
    Stop-Process -Id $_.Id -Force
}

Write-Host "Done."
