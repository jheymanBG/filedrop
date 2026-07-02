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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AdminAccessKey')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AdminAccessKey', N'ForGIS123!#%', 'Temporary local admin access key for /Admin until Entra admin consent is approved.');
END
ELSE
BEGIN
    UPDATE dbo.AppSettings
    SET SettingValue = N'ForGIS123!#%',
        ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Security.AdminAccessKey';
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Security.AdminSessionHours')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Security.AdminSessionHours', '8', 'Temporary admin session duration in hours.');
END
GO
