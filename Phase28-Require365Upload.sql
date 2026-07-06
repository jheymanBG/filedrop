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

IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.RequireMicrosoftLoginForUploads')
BEGIN
    UPDATE dbo.AppSettings SET SettingValue = 'true', ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Security.RequireMicrosoftLoginForUploads';
END
ELSE
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.RequireMicrosoftLoginForUploads', 'true', 'Require Microsoft 365 sign-in before creating transfers.');
END
GO

IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AllowTemporaryLocalUpload')
BEGIN
    UPDATE dbo.AppSettings SET SettingValue = 'false', ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Security.AllowTemporaryLocalUpload';
END
ELSE
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AllowTemporaryLocalUpload', 'false', 'Temporary unauthenticated uploads disabled.');
END
GO

IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.EntraAdminConsentGranted')
BEGIN
    UPDATE dbo.AppSettings SET SettingValue = 'true', ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Security.EntraAdminConsentGranted';
END
ELSE
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.EntraAdminConsentGranted', 'true', 'Admin consent has been granted.');
END
GO
