IF OBJECT_ID('dbo.FileScanResults', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileScanResults (
        ScanId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NULL,
        FileId UNIQUEIDENTIFIER NULL,
        OriginalFileName NVARCHAR(500) NOT NULL,
        StoragePath NVARCHAR(1000) NULL,
        Sha256Hash NVARCHAR(128) NULL,
        Engine NVARCHAR(100) NOT NULL,
        Result NVARCHAR(50) NOT NULL,
        ThreatName NVARCHAR(500) NULL,
        Details NVARCHAR(MAX) NULL,
        ScannedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FileScanResults_TransferId')
BEGIN
    CREATE INDEX IX_FileScanResults_TransferId ON dbo.FileScanResults(TransferId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FileScanResults_Result')
BEGIN
    CREATE INDEX IX_FileScanResults_Result ON dbo.FileScanResults(Result);
END
GO

IF OBJECT_ID('dbo.AppSettings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppSettings (
        SettingKey NVARCHAR(150) NOT NULL PRIMARY KEY,
        SettingValue NVARCHAR(MAX) NULL,
        Description NVARCHAR(500) NULL,
        ModifiedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.EnableVirusScanning')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.EnableVirusScanning', 'true', 'Run Microsoft Defender scan on uploaded files.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.QuarantineOnScanFailure')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.QuarantineOnScanFailure', 'true', 'Quarantine files when malware is detected or scan fails.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.BlockUnscannedFiles')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.BlockUnscannedFiles', 'true', 'Do not create a transfer if any uploaded file cannot be scanned.');
END
GO
