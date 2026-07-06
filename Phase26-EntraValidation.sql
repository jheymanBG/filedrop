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
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.LastSuccessfulMicrosoftLogin')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.LastSuccessfulMicrosoftLogin', '', 'Timestamp of last successful Microsoft sign-in observed by FileDrop.');
END
GO
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AutoSwitchToMicrosoftLogin')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AutoSwitchToMicrosoftLogin', 'false', 'Automatically require Microsoft login after successful validation.');
END
GO
