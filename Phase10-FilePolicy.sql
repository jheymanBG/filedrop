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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Uploads.MaxTotalUploadMB')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Uploads.MaxTotalUploadMB', '20480', 'Maximum total upload size per transfer in MB. Default is 20480 MB / 20 GB.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Uploads.MaxSingleFileMB')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Uploads.MaxSingleFileMB', '10240', 'Maximum size for one file in MB. Default is 10240 MB / 10 GB.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Uploads.BlockedExtensions')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Uploads.BlockedExtensions', '.exe,.bat,.cmd,.ps1,.vbs,.js,.msi,.scr,.com,.jar', 'Comma-separated extensions blocked from upload.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Uploads.RequireSubject')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Uploads.RequireSubject', 'false', 'Require users to enter a subject before uploading.');
END
GO
