# Isolated TestDrop IIS setup. Run as administrator with -WhatIf first.
[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [Parameter(Mandatory=$true)][string]$PublishedSource,
 [Parameter(Mandatory=$true)][string]$TestSettingsPath,
 [Parameter(Mandatory=$true)][string]$CertificateThumbprint
)
$ErrorActionPreference='Stop'
Import-Module WebAdministration
$site='FileDrop-Test'; $pool='FileDrop-Test'; $hostName='testdrop.bgohio.gov'
$dest='C:\Sites\TestDrop'; $store='C:\SecureFileTransfer-Test'
if (!(Test-Path "$PublishedSource\FileDrop.Web.dll")) { throw 'Verified published build not found.' }
if (!(Test-Path $TestSettingsPath)) { throw 'Host-local test config missing.' }
$config=Get-Content $TestSettingsPath -Raw | ConvertFrom-Json
if ($config.Application.HostName -ne $hostName) { throw 'Incorrect hostname.' }
if ($config.Storage.RootPath -ne $store) { throw 'Storage must be isolated.' }
foreach($key in @('FilesPath','TempPath','QuarantinePath','LogsPath')) {
 if (-not $config.Storage.$key.StartsWith($store + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Storage path $key is unsafe." }
}
$conn=$config.ConnectionStrings.DefaultConnection
if ($conn -notmatch '(?i)(Database|Initial Catalog)\s*=\s*FileDrop_Test\s*(;|$)') { throw 'Test database required.' }
if ($config.Notifications.SendDownloadNotification -ne $false -or $config.Notifications.SendSenderConfirmation -ne $false) { throw 'Outbound notifications must initially be disabled.' }
if (-not $config.AzureAd.ClientId -or -not $config.AzureAd.TenantId) { throw 'Dedicated test Entra configuration required.' }
$cert=Get-Item "Cert:\LocalMachine\My\$($CertificateThumbprint -replace ' ','')" -ErrorAction Stop
if (-not $cert.HasPrivateKey -or $cert.NotAfter -lt (Get-Date)) { throw 'Invalid certificate.' }
if (-not ($cert.DnsNameList | Where-Object { $_.Unicode -eq $hostName -or $_.Unicode -eq '*.bgohio.gov' })) { throw 'Certificate does not cover test hostname.' }
$prod=Get-Website -Name 'FileDrop' -ErrorAction SilentlyContinue
if ($prod -and $prod.physicalPath -eq $dest) { throw 'Production uses target directory.' }
$existing=Get-Website -Name $site -ErrorAction SilentlyContinue
if ($existing -and $existing.physicalPath -ne $dest) { throw 'Test site path collision.' }
$binding=Get-WebBinding -Protocol https | Where-Object { $_.bindingInformation -like "*:443:$hostName" }
if ($binding -and -not $existing) { throw 'HTTPS hostname already in use.' }
if (-not $PSCmdlet.ShouldProcess($site,'Install isolated test site')) { return }
foreach($p in @($dest,$store,"$store\Files","$store\Temp","$store\Quarantine","$store\Logs")) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
if (!(Test-Path "IIS:\AppPools\$pool")) { New-WebAppPool -Name $pool | Out-Null }
Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.identityType -Value 4
if ($existing) { Stop-WebAppPool -Name $pool -ErrorAction SilentlyContinue }
& robocopy $PublishedSource $dest /E /XF appsettings.Production.json appsettings.TestDrop.json web.config | Out-Null
if ($LASTEXITCODE -gt 7) { throw "Copy failed: $LASTEXITCODE" }
Copy-Item $TestSettingsPath "$dest\appsettings.TestDrop.json" -Force
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <location path="." inheritInChildApplications="false">
  <system.webServer>
   <handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
   <aspNetCore processPath="dotnet" arguments=".\FileDrop.Web.dll" hostingModel="inprocess" stdoutLogEnabled="false">
    <environmentVariables><environmentVariable name="ASPNETCORE_ENVIRONMENT" value="TestDrop" /></environmentVariables>
   </aspNetCore>
  </system.webServer>
 </location>
</configuration>
'@ | Set-Content "$dest\web.config" -Encoding UTF8
& icacls $dest /grant "IIS AppPool\${pool}:(OI)(CI)RX" /T | Out-Null
& icacls $store /grant "IIS AppPool\${pool}:(OI)(CI)M" /T | Out-Null
if (-not $existing) { New-Website -Name $site -PhysicalPath $dest -ApplicationPool $pool -IPAddress '127.0.0.1' -Port 8099 | Out-Null }
if (-not (Get-WebBinding -Name $site -Protocol https -ErrorAction SilentlyContinue | Where-Object { $_.bindingInformation -like "*:443:$hostName" })) {
 New-WebBinding -Name $site -Protocol https -IPAddress '*' -Port 443 -HostHeader $hostName -SslFlags 1
}
New-Item "IIS:\SslBindings\0.0.0.0!443!$hostName" -Value $cert -SSLFlags 1 -Force | Out-Null
Start-WebAppPool -Name $pool
Start-Website -Name $site
Write-Host "TestDrop IIS setup complete; test DNS, SQL, Entra and production health."
