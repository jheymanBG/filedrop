# FileDrop Install.ps1
# Windows 11 Pro + IIS + SQL Express + ASP.NET Core
# Run as Administrator

$ErrorActionPreference = "Stop"

$AppName       = "FileDrop"
$SiteName      = "FileDrop"
$AppPoolName   = "FileDrop"
$Port          = 8080

$BuildRoot     = "C:\Build\FileDrop_v1_starter"
$SourceRoot    = "$BuildRoot\src"
$ProjectPath   = "$SourceRoot\FileDrop.Web\FileDrop.Web.csproj"
$PublishPath   = "$BuildRoot\Publish"

$StorageRoot   = "C:\SecureFileTransfer"
$SqlServer     = "localhost\SQLEXPRESS"
$DatabaseName  = "FileDrop"
$SqlLogin      = "filedrop"
$LocalSvcUser  = "FileDropSvc"

$BccEmail      = "jason.heyman@bgohio.gov"
$DisplaySender = "GIS Department"

function ConvertTo-PlainText {
    param([securestring]$SecureString)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Enable-IISFeature {
    param([string]$FeatureName)
    Write-Host "Enabling $FeatureName"
    Enable-WindowsOptionalFeature -Online -FeatureName $FeatureName -All -NoRestart | Out-Null
}

Write-Host "FileDrop installer starting..." -ForegroundColor Cyan

$LocalSvcPassword = Read-Host "Enter password for local IIS account .\$LocalSvcUser" -AsSecureString
$SqlPassword      = Read-Host "Enter password to create/use SQL login $SqlLogin" -AsSecureString

$LocalSvcPasswordPlain = ConvertTo-PlainText $LocalSvcPassword
$SqlPasswordPlain      = ConvertTo-PlainText $SqlPassword
$SqlPasswordEscaped    = $SqlPasswordPlain.Replace("'", "''")

Write-Host "Enabling IIS features for Windows 11..." -ForegroundColor Cyan

$features = @(
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-CommonHttpFeatures",
    "IIS-DefaultDocument",
    "IIS-StaticContent",
    "IIS-HttpErrors",
    "IIS-HttpLogging",
    "IIS-RequestFiltering",
    "IIS-ManagementConsole",
    "IIS-ManagementScriptingTools",
    "IIS-ASPNET45",
    "IIS-NetFxExtensibility45",
    "IIS-ISAPIExtensions",
    "IIS-ISAPIFilter",
    "IIS-WebSockets"
)

foreach ($f in $features) { Enable-IISFeature $f }

Import-Module WebAdministration

$DotNet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $DotNet) { $DotNet = "C:\Program Files\dotnet\dotnet.exe" }
if (!(Test-Path $DotNet)) {
    throw ".NET SDK was not found. Install .NET 9 SDK and rerun."
}

$AspNetCoreModule = & "$env:windir\System32\inetsrv\appcmd.exe" list modules | Select-String "AspNetCoreModuleV2"
if (-not $AspNetCoreModule) {
    throw "ASP.NET Core Hosting Bundle is missing. Install .NET 9 Hosting Bundle and rerun."
}

Write-Host "Creating folders..." -ForegroundColor Cyan

$folders = @(
    $BuildRoot,
    $PublishPath,
    $StorageRoot,
    "$StorageRoot\Files",
    "$StorageRoot\Temp",
    "$StorageRoot\Logs",
    "$StorageRoot\Quarantine",
    "$StorageRoot\Config",
    "$StorageRoot\Backups"
)

foreach ($folder in $folders) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

Write-Host "Creating/updating local IIS account..." -ForegroundColor Cyan

if (-not (Get-LocalUser -Name $LocalSvcUser -ErrorAction SilentlyContinue)) {
    New-LocalUser `
        -Name $LocalSvcUser `
        -Password $LocalSvcPassword `
        -FullName "FileDrop IIS Service Account" `
        -Description "Runs FileDrop IIS application pool"
}
Set-LocalUser -Name $LocalSvcUser -PasswordNeverExpires $true

Write-Host "Writing appsettings.Production.json..." -ForegroundColor Cyan

$config = @{
    Application = @{
        Name = $AppName
        HostName = "filedrop.bgohio.gov"
        DisplaySender = $DisplaySender
    }
    Storage = @{
        RootPath = $StorageRoot
        FilesPath = "$StorageRoot\Files"
        TempPath = "$StorageRoot\Temp"
        QuarantinePath = "$StorageRoot\Quarantine"
        LogsPath = "$StorageRoot\Logs"
    }
    Transfers = @{
        DefaultExpirationDays = 7
        MaximumExpirationDays = 30
    }
    Notifications = @{
        AlwaysBcc = $BccEmail
        SendDownloadNotification = $true
        SendSenderConfirmation = $true
    }
    ConnectionStrings = @{
        DefaultConnection = "Server=$SqlServer;Database=$DatabaseName;User ID=$SqlLogin;Password=$SqlPasswordPlain;TrustServerCertificate=True;Encrypt=True;"
    }
}

$config | ConvertTo-Json -Depth 10 | Set-Content "$SourceRoot\FileDrop.Web\appsettings.Production.json" -Encoding UTF8

Write-Host "Creating SQL database/login/tables..." -ForegroundColor Cyan

$sqlScript = @"
IF DB_ID('$DatabaseName') IS NULL
BEGIN
    CREATE DATABASE [$DatabaseName];
END
GO

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = '$SqlLogin')
BEGIN
    CREATE LOGIN [$SqlLogin]
    WITH PASSWORD = '$SqlPasswordEscaped',
    CHECK_POLICY = ON,
    CHECK_EXPIRATION = OFF;
END
GO

USE [$DatabaseName];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$SqlLogin')
BEGIN
    CREATE USER [$SqlLogin] FOR LOGIN [$SqlLogin];
END
GO

ALTER ROLE db_datareader ADD MEMBER [$SqlLogin];
ALTER ROLE db_datawriter ADD MEMBER [$SqlLogin];
GO

IF OBJECT_ID('dbo.Transfers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Transfers (
        TransferId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        SenderEmail NVARCHAR(255) NOT NULL,
        SenderName NVARCHAR(255) NULL,
        RecipientEmail NVARCHAR(255) NOT NULL,
        Subject NVARCHAR(255) NULL,
        Message NVARCHAR(MAX) NULL,
        DownloadToken NVARCHAR(128) NOT NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ExpirationDate DATETIME2 NOT NULL,
        FirstDownloadDate DATETIME2 NULL,
        LastDownloadDate DATETIME2 NULL,
        DownloadCount INT NOT NULL DEFAULT 0,
        Status NVARCHAR(50) NOT NULL DEFAULT 'Active'
    );
END
GO

IF OBJECT_ID('dbo.TransferFiles', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TransferFiles (
        FileId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NOT NULL,
        OriginalFileName NVARCHAR(512) NOT NULL,
        StoredFileName NVARCHAR(512) NOT NULL,
        StoragePath NVARCHAR(1000) NOT NULL,
        ContentType NVARCHAR(255) NULL,
        FileSizeBytes BIGINT NOT NULL,
        Sha256Hash NVARCHAR(128) NULL,
        UploadedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ScanStatus NVARCHAR(50) NULL,
        CONSTRAINT FK_TransferFiles_Transfers
            FOREIGN KEY (TransferId) REFERENCES dbo.Transfers(TransferId)
    );
END
GO

IF OBJECT_ID('dbo.AuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog (
        AuditId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NULL,
        UserEmail NVARCHAR(255) NULL,
        Action NVARCHAR(100) NOT NULL,
        Details NVARCHAR(MAX) NULL,
        IpAddress NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Transfers_DownloadToken')
BEGIN
    CREATE INDEX IX_Transfers_DownloadToken ON dbo.Transfers(DownloadToken);
END
GO
"@

$sqlPath = "$BuildRoot\Create-Database.sql"
$sqlScript | Set-Content $sqlPath -Encoding UTF8

sqlcmd -S $SqlServer -E -C -i $sqlPath

Write-Host "Publishing FileDrop..." -ForegroundColor Cyan
& $DotNet publish $ProjectPath -c Release -o $PublishPath

Write-Host "Writing web.config..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path "$PublishPath\logs" -Force | Out-Null

@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath=".\FileDrop.Web.exe"
                  stdoutLogEnabled="true"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
'@ | Set-Content "$PublishPath\web.config" -Encoding UTF8

Write-Host "Setting NTFS permissions..." -ForegroundColor Cyan
$svc = ".\$LocalSvcUser"
icacls $PublishPath /grant "$($svc):(OI)(CI)RX" /T | Out-Null
icacls $StorageRoot /grant "$($svc):(OI)(CI)M" /T | Out-Null

Write-Host "Configuring IIS..." -ForegroundColor Cyan

if (!(Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}

Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 3
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName -Value ".\$LocalSvcUser"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value $LocalSvcPasswordPlain

if (Get-Website -Name $SiteName -ErrorAction SilentlyContinue) {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PublishPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}
else {
    New-Website `
        -Name $SiteName `
        -PhysicalPath $PublishPath `
        -ApplicationPool $AppPoolName `
        -Port $Port `
        -Force | Out-Null
}

& "$env:windir\System32\inetsrv\appcmd.exe" recycle apppool /apppool.name:"$AppPoolName" | Out-Null
& "$env:windir\System32\inetsrv\appcmd.exe" start site /site.name:"$SiteName" | Out-Null

Write-Host ""
Write-Host "FileDrop install complete." -ForegroundColor Green
Write-Host "Test URL: http://localhost:$Port"
Write-Host "Storage: $StorageRoot"
Write-Host "Publish: $PublishPath"
Write-Host "SQL: $SqlServer / $DatabaseName"
