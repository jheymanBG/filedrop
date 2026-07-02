param(
    [string]$Root = "C:\Build\FileDrop_v1_starter",
    [string]$Project = "C:\Build\FileDrop_v1_starter\src\FileDrop.Web",
    [string]$Publish = "C:\Build\FileDrop_v1_starter\Publish",
    [string]$Staging = "C:\Build\FileDrop_v1_starter\Staging",
    [string]$AppPool = "FileDrop",
    [string]$HealthUrl = "http://localhost:8080/Admin/Health"
)

$ErrorActionPreference = "Stop"

$deployStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupScript = Join-Path $Root "DeployTools\Backup-FileDrop.ps1"

Write-Host "Starting safe FileDrop deployment..." -ForegroundColor Cyan

if (Test-Path $backupScript) {
    & $backupScript
}

if (Test-Path $Staging) {
    Remove-Item $Staging -Recurse -Force
}

New-Item -ItemType Directory -Path $Staging -Force | Out-Null

Write-Host "Publishing to staging..." -ForegroundColor Cyan
Set-Location $Project
dotnet publish -c Release -o $Staging

Write-Host "Stopping IIS app pool..." -ForegroundColor Cyan
& "$env:windir\System32\inetsrv\appcmd.exe" stop apppool /apppool.name:"$AppPool" | Out-Null

try {
    Write-Host "Copying staging to publish folder..." -ForegroundColor Cyan

    if (-not (Test-Path $Publish)) {
        New-Item -ItemType Directory -Path $Publish -Force | Out-Null
    }

    Remove-Item "$Publish\*" -Recurse -Force
    Copy-Item "$Staging\*" $Publish -Recurse -Force
}
finally {
    Write-Host "Starting IIS app pool..." -ForegroundColor Cyan
    & "$env:windir\System32\inetsrv\appcmd.exe" start apppool /apppool.name:"$AppPool" | Out-Null
}

Start-Sleep -Seconds 3

try {
    $response = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 15
    Write-Host "Health check HTTP $($response.StatusCode)" -ForegroundColor Green
}
catch {
    Write-Warning "Health check failed: $($_.Exception.Message)"
    Write-Warning "Use Restore-FileDrop.ps1 if rollback is needed."
    throw
}

Write-Host ""
Write-Host "Safe deployment complete." -ForegroundColor Green
