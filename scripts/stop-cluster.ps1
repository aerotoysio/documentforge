# Stops all locally-running DocumentForge.Api nodes.
# Kills any process matching "DocumentForge.Api" - use with caution if you have
# other dotnet projects running.

Write-Host "Stopping DocumentForge nodes..." -ForegroundColor Yellow

Get-Process | Where-Object { $_.ProcessName -eq 'DocumentForge.Api' } | ForEach-Object {
    Write-Host "  Killing PID $($_.Id)"
    Stop-Process -Id $_.Id -Force
}

Write-Host "Done."
