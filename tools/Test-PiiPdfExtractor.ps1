#requires -Version 5.1
<#
Read-only regression test for the missing-font PDF extraction failure.
Builds the current source and invokes PiiScanService.ExtractPdf on an already-held
production PDF without writing extracted text or modifying any application data.
This does NOT change PII review decisions or deploy to IIS.
#>
param(
    [string]$UploadId = 'c3497893-a87e-4910-9147-042cbd02f609',
    [string]$ProductionSite = 'C:\Sites\FileDrop-Recovery'
)
$ErrorActionPreference = 'Stop'
$Project = Join-Path $PSScriptRoot '..\src\FileDrop.Web\FileDrop.Web.csproj'
$QaRoot = Join-Path $env:TEMP ('FileDrop-PdfRegression-' + [guid]::NewGuid().ToString('N'))
$Db = $null
try {
    if (-not (Test-Path -LiteralPath $Project)) { throw 'Source project not found.' }
    Write-Host '=== PDF PII EXTRACTION REGRESSION QA ===' -ForegroundColor Cyan
    $branch = (& git -C (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) branch --show-current)
    # Branch is displayed only for reference; the invoking script checks the commit.
    Write-Host ('Source branch: ' + ($branch -join '').Trim())

    $connection = $null
    foreach ($name in @('appsettings.json', 'appsettings.Production.json')) {
        $configFile = Join-Path $ProductionSite $name
        if (-not (Test-Path -LiteralPath $configFile)) { continue }
        $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
        if ($config.ConnectionStrings -and $config.ConnectionStrings.DefaultConnection) {
            $candidate = [string]$config.ConnectionStrings.DefaultConnection
            $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($candidate)
            if ($builder.InitialCatalog -ine 'FileDrop') { throw 'Unexpected database. Aborting.' }
            $connection = $candidate
        }
    }
    if (-not $connection) { throw 'Production DB configuration not found.' }
    $Db = [System.Data.SqlClient.SqlConnection]::new($connection)
    $Db.Open()
    if ($Db.Database -ine 'FileDrop') { throw 'Unexpected SQL database. Aborting.' }

    $command = $Db.CreateCommand()
    $command.CommandText = @'
SELECT TOP 1 FinalStoragePath
FROM dbo.ChunkedUploadSessions
WHERE UploadId = @UploadId
  AND OriginalFileName LIKE '%.pdf'
  AND Status = 'PendingPIIReview'
  AND FinalStoragePath IS NOT NULL
'@
    [void]$command.Parameters.AddWithValue('@UploadId', [guid]$UploadId)
    $sourcePdf = [string]$command.ExecuteScalar()
    $command.Dispose()
    if (-not $sourcePdf -or -not (Test-Path -LiteralPath $sourcePdf -PathType Leaf)) {
        throw 'Held PDF not found or not pending review. Nothing scanned.'
    }
    $pdfInfo = Get-Item -LiteralPath $sourcePdf
    Write-Host ('Held PDF found. Size MB: ' + [math]::Round($pdfInfo.Length / 1MB, 2))

    New-Item -ItemType Directory -Path $QaRoot -Force | Out-Null
    $testProject = Join-Path $QaRoot 'PdfRegression.csproj'
    $projectPath = [System.Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $Project).Path)
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$projectPath" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $testProject -Encoding UTF8
    $testProgram = Join-Path $QaRoot 'Program.cs'
    @'
using System;
using System.Reflection;
using FileDrop.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

try
{
    var path = args[0];
    // The scanner's extraction stage is invoked directly; no DB writes, PII release,
    // provider calls, outgoing emails, or extracted text output can occur.
    var scanner = new PiiScanService(null!, null!, NullLogger<PiiScanService>.Instance);
    var method = typeof(PiiScanService).GetMethod("ExtractPdf", BindingFlags.Instance | BindingFlags.NonPublic);
    if (method is null) throw new InvalidOperationException("ExtractPdf method not found.");
    var outcome = method.Invoke(scanner, new object[] { path, 2_000_000, 250 });
    if (outcome is null) throw new InvalidOperationException("No extraction result.");
    var type = outcome.GetType();
    var incomplete = (int)(type.GetProperty("UnsupportedItems")?.GetValue(outcome) ?? -1);
    var text = (string)(type.GetProperty("Text")?.GetValue(outcome) ?? "");
    Console.WriteLine($"Readable characters (never displayed): {text.Length}");
    Console.WriteLine($"Uninspectable pages/sections: {incomplete}");
    if (incomplete <= 0)
    {
        Console.WriteLine("FAIL: Missing-font regression was not reproduced as incomplete.");
        Environment.ExitCode = 2;
    }
    else
    {
        Console.WriteLine("PASS: Parser returned partial extraction without crashing.");
        Console.WriteLine("Important: incomplete PDFs must remain blocked from automatic clearance.");
    }
}
catch (TargetInvocationException ex)
{
    Console.WriteLine("FAIL: PDF extraction threw " + (ex.InnerException?.GetType().Name ?? "unknown exception"));
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL: " + ex.GetType().Name);
    Environment.ExitCode = 1;
}
'@ | Set-Content -LiteralPath $testProgram -Encoding UTF8

    Write-Host 'Building source and regression harness...'
    & dotnet build $testProject --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Regression harness build failed.' }

    Write-Host 'Running read-only parser test against held PDF...'
    & dotnet run --project $testProject --configuration Release --no-build -- $sourcePdf
    if ($LASTEXITCODE -ne 0) { throw 'PDF parser regression test FAILED. No deployment was performed.' }
    Write-Host 'REGRESSION PASS. No production state changed.' -ForegroundColor Green
}
finally {
    if ($Db) { $Db.Dispose() }
    if (Test-Path -LiteralPath $QaRoot) {
        Remove-Item -LiteralPath $QaRoot -Recurse -Force
    }
}
