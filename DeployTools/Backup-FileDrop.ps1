param(
    [string]$Root = "C:\Build\FileDrop_v1_starter",
    [string]$Publish = "C:\Build\FileDrop_v1_starter\Publish",
    [string]$Storage = "C:\SecureFileTransfer",
    [string]$SqlServer = "localhost\SQLEXPRESS",
    [string]$Database = "FileDrop"
)

$ErrorActionPreference = "Stop"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $Root "Backups\$stamp"

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

Write-Host "Creating FileDrop backup: $backupRoot" -ForegroundColor Cyan

if (Test-Path $Publish) {
    $publishZip = Join-Path $backupRoot "Publish.zip"
    Compress-Archive -Path (Join-Path $Publish "*") -DestinationPath $publishZip -Force
}

$configFiles = @(
    (Join-Path $Publish "appsettings.Production.json"),
    (Join-Path $Publish "web.config"),
    (Join-Path $Root "src\FileDrop.Web\appsettings.Production.json")
)

$configFolder = Join-Path $backupRoot "Config"
New-Item -ItemType Directory -Path $configFolder -Force | Out-Null

foreach ($file in $configFiles) {
    if (Test-Path $file) {
        Copy-Item $file -Destination $configFolder -Force
    }
}

$dbBackup = Join-Path $backupRoot "$Database.bak"
$sql = "BACKUP DATABASE [$Database] TO DISK = N'$dbBackup' WITH INIT, STATS = 10"
sqlcmd -S $SqlServer -E -C -Q $sql

$manifest = [pscustomobject]@{
    Created = (Get-Date).ToString("o")
    Root = $Root
    Publish = $Publish
    Storage = $Storage
    SqlServer = $SqlServer
    Database = $Database
    BackupFolder = $backupRoot
    PublishZip = "Publish.zip"
    DatabaseBackup = "$Database.bak"
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $backupRoot "manifest.json") -Encoding UTF8

Write-Host "Backup complete: $backupRoot" -ForegroundColor Green
