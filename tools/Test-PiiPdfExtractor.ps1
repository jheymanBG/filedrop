#requires -Version 5.1
<#
Read-only end-to-end PII scanner decision and PDF page-limit regression checks.
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
    $branch = (& git -C (Split-Path $PSScriptRoot -Parent) branch --show-current)
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
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileDrop.Web.Models;
using FileDrop.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

var path = args[0];
const int pageLimit = 1000;

try
{
    using var document = PdfDocument.Open(path);
    var pageCount = document.NumberOfPages;
    var beyondPageLimit = Math.Max(0, pageCount - pageLimit);
    Console.WriteLine($"PDF total pages: {pageCount}");
    Console.WriteLine($"Configured test page limit: {pageLimit}");
    Console.WriteLine($"Pages beyond limit (never inspected): {beyondPageLimit}");

    // Stub settings exercise the real ScanAsync decision path without touching SQL,
    // invoking Presidio, transmitting contents, or changing PII review records.
    // Deliberately turn OFF general BlockUnscannable and BlockOnFailure: PDFs must
    // still fail closed when extraction is incomplete or broken.
    var settings = new OfflineSettings("BuiltIn");
    var scanner = new PiiScanService(settings, null!, NullLogger<PiiScanService>.Instance);
    var result = await scanner.ScanAsync(path, Path.GetFileName(path), CancellationToken.None);

    Console.WriteLine($"PII result: {result.Result}");
    Console.WriteLine($"PII allowed: {result.Allowed}");
    Console.WriteLine($"Extracted characters (no content shown): {result.ExtractedCharacters}");
    Console.WriteLine($"Total uninspected pages/sections: {result.UnsupportedItems}");
    Console.WriteLine($"Extraction breakdown: {result.Message}");
    Console.WriteLine($"Uninspected within first {pageLimit} pages or due to extraction limit: {Math.Max(0, result.UnsupportedItems - beyondPageLimit)}");

    if (result.UnsupportedItems <= 0 || result.Allowed || result.Result != "Blocked")
        throw new InvalidOperationException("FAIL: Incomplete PDF was not blocked by ScanAsync.");
    Console.WriteLine("PASS: Incomplete held PDF is blocked despite relaxed generic scanning settings.");

    // Separate synthetic corrupt PDF verifies fail-closed exception handling when
    // BlockOnFailure=false; synthetic input contains no user document data.
    var corruptPdf = Path.Combine(Path.GetTempPath(), "filedrop-invalid-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
    try
    {
        File.WriteAllText(corruptPdf, "This is not a PDF file.");
        var badResult = await scanner.ScanAsync(corruptPdf, "invalid.pdf", CancellationToken.None);
        Console.WriteLine($"Corrupt PDF result: {badResult.Result}; Allowed: {badResult.Allowed}");
        if (badResult.Allowed || badResult.Result != "Failed")
            throw new InvalidOperationException("FAIL: Corrupt PDF was allowed.");
        Console.WriteLine("PASS: Corrupt PDF fails closed with BlockOnFailure=false.");
    }
    finally { if (File.Exists(corruptPdf)) File.Delete(corruptPdf); }

    // Unsupported file types must be retained for review, never silently cleared.
    var unsupported = Path.Combine(Path.GetTempPath(), "filedrop-unsupported-" + Guid.NewGuid().ToString("N") + ".unknown");
    try
    {
        File.WriteAllText(unsupported, "Synthetic unsupported-file regression");
        var unknownResult = await scanner.ScanAsync(unsupported, "sample.unknown", CancellationToken.None);
        Console.WriteLine($"Unsupported file result: {unknownResult.Result}; Allowed: {unknownResult.Allowed}");
        if (unknownResult.Allowed || unknownResult.Result != "Unscannable")
            throw new InvalidOperationException("FAIL: Unsupported file type was allowed.");
        Console.WriteLine("PASS: Unsupported file types require review.");
    }
    finally { if (File.Exists(unsupported)) File.Delete(unsupported); }

    Console.WriteLine("PASS: PDF scan decision regression QA complete; no production state changed.");
}
catch (Exception ex)
{
    Console.WriteLine("FAIL: " + ex.GetType().Name + ": PDF regression assertion failed.");
    Environment.ExitCode = 1;
}

sealed class OfflineSettings : ISettingsRepository
{
    private readonly string _provider;
    public OfflineSettings(string provider) => _provider = provider;
    public Task<string?> GetValueAsync(string key) => Task.FromResult<string?>(null);
    public Task<string> GetStringAsync(string key, string fallback)
        => Task.FromResult(key == "Security.PiiScanning.Provider" ? _provider : fallback);
    public Task<bool> GetBoolAsync(string key, bool fallback)
        => Task.FromResult(key is "Security.PiiScanning.BlockUnscannable" or "Security.PiiScanning.BlockOnFailure" ? false : fallback);
    public Task<int> GetIntAsync(string key, int fallback) => Task.FromResult(fallback);
    public Task<List<AppSettingRecord>> GetAllAsync() => throw new NotSupportedException("Read-only QA");
    public Task UpdateAsync(string key, string? value) => throw new NotSupportedException("Read-only QA");
    public Task UpsertAsync(string key, string? value, string? description = null)
        => throw new NotSupportedException("Read-only QA");
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
    Write-Host ('QA harness retained at: ' + $QaRoot)
}
