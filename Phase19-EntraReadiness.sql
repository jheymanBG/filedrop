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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.RequireMicrosoftLoginForUploads')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.RequireMicrosoftLoginForUploads', 'false', 'Require Microsoft 365 sign-in before creating transfers. Set true after Entra admin consent is approved.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AllowTemporaryLocalUpload')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AllowTemporaryLocalUpload', 'true', 'Allow temporary unauthenticated uploads with manual sender name/email while Entra consent is pending.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.EntraAdminConsentGranted')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.EntraAdminConsentGranted', 'false', 'Manual flag indicating tenant admin consent has been granted for FileDrop.');
END
GO
