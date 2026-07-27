<#
.SYNOPSIS
  Build the DocumentForge Studio Windows installer (self-contained, no .NET
  runtime required on the target).

.DESCRIPTION
  1. Publishes DocumentForge Studio self-contained for win-x64 into dist\studio.
  2. Publishes the dfdb service self-contained into dist\studio\service.
  3. Compiles installer\DocumentForgeStudio.iss with Inno Setup (ISCC) into
     dist\installer\DocumentForgeStudio-<version>-setup.exe.

.PARAMETER Version
  Version stamped into the installer filename/metadata. Defaults to 0.12.0.

.EXAMPLE
  pwsh scripts\build-studio-installer.ps1
#>
param(
    [string]$Version = "0.12.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"
$studioOut = Join-Path $dist "studio"
$serviceOut = Join-Path $dist "service"

Write-Host "==> Cleaning staging folders" -ForegroundColor Cyan
if (Test-Path $studioOut) { Remove-Item -Recurse -Force $studioOut }
if (Test-Path $serviceOut) { Remove-Item -Recurse -Force $serviceOut }
New-Item -ItemType Directory -Force $studioOut | Out-Null
New-Item -ItemType Directory -Force $serviceOut | Out-Null

Write-Host "==> Publishing DocumentForge Studio ($Runtime, self-contained)" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\DocumentForge.Studio\DocumentForge.Studio.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:Version=$Version `
    -o $studioOut
if ($LASTEXITCODE -ne 0) { throw "Studio publish failed." }

Write-Host "==> Publishing dfdb service ($Runtime, self-contained)" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\DocumentForge.Cli\DocumentForge.Cli.csproj") `
    -c Release -r $Runtime --self-contained true `
    -o $serviceOut
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

Write-Host "==> Locating Inno Setup compiler (ISCC)" -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install Inno Setup 6, then re-run."
}

Write-Host "==> Compiling installer with $iscc" -ForegroundColor Cyan
$iss = Join-Path $repo "installer\DocumentForgeStudio.iss"
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Join-Path $dist "installer\DocumentForgeStudio-$Version-setup.exe"
Write-Host ""
Write-Host "==> Done: $setup" -ForegroundColor Green
