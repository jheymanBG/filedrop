param(
    [Parameter(Mandatory=$true)]
    [string]$BackupFolder,

    [string]$Root = "C:\Build\FileDrop_v1_starter",
    [string]$Publish = "C:\Build\FileDrop_v1_starter\Publish",
    [string]$AppPool = "FileDrop",
    [string]$SqlServer = "localhost\SQLEXPRESS",
    [string]$Database = "FileDrop",
    [switch]$RestoreDatabase
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BackupFolder)) {
    throw "Backup folder not found: $BackupFolder"
}

Write-Host "Restoring FileDrop from: $BackupFolder" -ForegroundColor Cyan

& "$env:windir\System32\inetsrv\appcmd.exe" stop apppool /apppool.name:"$AppPool" | Out-Null

try {
    $publishZip = Join-Path $BackupFolder "Publish.zip"

    if (Test-Path $publishZip) {
        $temp = Join-Path $BackupFolder "RestoreTemp"

        if (Test-Path $temp) {
            Remove-Item $temp -Recurse -Force
        }

        New-Item -ItemType Directory -Path $temp -Force | Out-Null
        Expand-Archive -Path $publishZip -DestinationPath $temp -Force

        if (Test-Path $Publish) {
            Remove-Item "$Publish\*" -Recurse -Force
        } else {
            New-Item -ItemType Directory -Path $Publish -Force | Out-Null
        }

        Copy-Item "$temp\*" $Publish -Recurse -Force
        Write-Host "Publish folder restored."
    }

    if ($RestoreDatabase) {
        $dbBackup = Join-Path $BackupFolder "$Database.bak"

        if (-not (Test-Path $dbBackup)) {
            throw "Database backup not found: $dbBackup"
        }

        $sql = @"
ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [$Database] FROM DISK = N'$dbBackup' WITH REPLACE;
ALTER DATABASE [$Database] SET MULTI_USER;
"@

        sqlcmd -S $SqlServer -E -C -Q $sql
        Write-Host "Database restored."
    }
}
finally {
    & "$env:windir\System32\inetsrv\appcmd.exe" start apppool /apppool.name:"$AppPool" | Out-Null
}

Write-Host ""
Write-Host "Restore complete." -ForegroundColor Green
