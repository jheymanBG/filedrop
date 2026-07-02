IF COL_LENGTH('dbo.Transfers', 'MaxDownloads') IS NULL
BEGIN
    ALTER TABLE dbo.Transfers ADD MaxDownloads INT NULL;
END
GO

IF COL_LENGTH('dbo.Transfers', 'DisableAfterFirstDownload') IS NULL
BEGIN
    ALTER TABLE dbo.Transfers ADD DisableAfterFirstDownload BIT NOT NULL CONSTRAINT DF_Transfers_DisableAfterFirstDownload DEFAULT 0;
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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AllowOneTimeLinks')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AllowOneTimeLinks', 'true', 'Allow senders/admins to create links that disable after the first download.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.DefaultOneTimeLink')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.DefaultOneTimeLink', 'false', 'Default new transfers to one-time download links.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.DefaultMaxDownloads')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.DefaultMaxDownloads', '', 'Optional default maximum downloads per transfer. Blank means unlimited.');
END
GO
