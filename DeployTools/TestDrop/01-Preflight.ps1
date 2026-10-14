# Run in elevated Windows PowerShell 5.1 on the FileDrop IIS server.
$ErrorActionPreference='Stop'
Write-Host '=== TestDrop local preflight (read-only) ===' -ForegroundColor Cyan
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated.' }
Import-Module WebAdministration -ErrorAction Stop
$prod=Get-Website -Name FileDrop -ErrorAction SilentlyContinue
if (-not $prod) { throw 'Expected production IIS site FileDrop not found. Investigate before continuing.' }
Write-Host "Production site: $($prod.Name); state $($prod.State); path $($prod.physicalPath)"
Write-Host "Production bindings: $((Get-WebBinding -Name FileDrop | ForEach-Object { $_.bindingInformation }) -join ', ')"
if ((Get-WebBinding | Where-Object { $_.bindingInformation -match '(^|:)8443:' -and $_.ItemXPath -notmatch "name='FileDrop-Test'" })) { throw 'Port 8443 is already claimed by another site.' }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { Write-Warning 'sqlcmd not found. Install SQL command-line utilities before running 02.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Warning 'dotnet missing; install compatible ASP.NET Core hosting bundle.' }
if (-not (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=localhost' -and $_.NotAfter -gt (Get-Date) })) { Write-Warning 'No local HTTPS certificate found; setup creates a localhost test certificate.' }
Write-Host 'Preflight completed. Read-only: no IIS, SQL, or file changes were made.' -ForegroundColor Green
