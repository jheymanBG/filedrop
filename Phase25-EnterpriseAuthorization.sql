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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.Mode')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.Mode', 'TemporaryAdminKey', 'Authorization mode: TemporaryAdminKey, EntraGroups, or Hybrid.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.AdminGroupObjectId')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.AdminGroupObjectId', '', 'Microsoft Entra group object ID for FileDrop Administrators.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.AuditorGroupObjectId')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.AuditorGroupObjectId', '', 'Microsoft Entra group object ID for FileDrop Auditors.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.HelpDeskGroupObjectId')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.HelpDeskGroupObjectId', '', 'Microsoft Entra group object ID for FileDrop Help Desk users.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.RequireAdminGroupForAdminPortal')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.RequireAdminGroupForAdminPortal', 'false', 'Require Entra admin group membership for /Admin. Set true after Entra admin consent works.');
END
GO

IF OBJECT_ID('dbo.AuthorizationEvents', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuthorizationEvents (
        AuthorizationEventId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        UserEmail NVARCHAR(255) NULL,
        UserName NVARCHAR(255) NULL,
        RequiredRole NVARCHAR(100) NULL,
        Authorized BIT NOT NULL,
        Reason NVARCHAR(MAX) NULL,
        IpAddress NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuthorizationEvents_CreatedDate')
BEGIN
    CREATE INDEX IX_AuthorizationEvents_CreatedDate ON dbo.AuthorizationEvents(CreatedDate);
END
GO
