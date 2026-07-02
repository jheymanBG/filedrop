param(
    [string]$Root = "C:\Build\FileDrop_v1_starter"
)

$backupRoot = Join-Path $Root "Backups"

if (-not (Test-Path $backupRoot)) {
    Write-Host "No backups folder found."
    return
}

Get-ChildItem $backupRoot -Directory |
    Sort-Object Name -Descending |
    Select-Object Name, FullName, LastWriteTime
