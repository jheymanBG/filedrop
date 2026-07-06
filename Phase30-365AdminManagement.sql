IF OBJECT_ID('dbo.FileDropAdminUsers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileDropAdminUsers (
        AdminUserId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Email NVARCHAR(255) NOT NULL UNIQUE,
        DisplayName NVARCHAR(255) NULL,
        RoleName NVARCHAR(100) NOT NULL DEFAULT 'Administrator',
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.FileDropAdminUsers WHERE Email = 'jheyman@bgohio.gov')
BEGIN
    INSERT INTO dbo.FileDropAdminUsers (Email, DisplayName, RoleName, IsActive)
    VALUES ('jheyman@bgohio.gov', 'Jason Heyman', 'Administrator', 1);
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.FileDropAdminUsers WHERE Email = 'jason.heyman@bgohio.gov')
BEGIN
    INSERT INTO dbo.FileDropAdminUsers (Email, DisplayName, RoleName, IsActive)
    VALUES ('jason.heyman@bgohio.gov', 'Jason Heyman', 'Administrator', 1);
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

IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.Mode')
BEGIN
    UPDATE dbo.AppSettings SET SettingValue = 'Microsoft365Admins', ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Authorization.Mode';
END
ELSE
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Authorization.Mode', 'Microsoft365Admins', 'Admin portal uses Microsoft 365 sign-in plus FileDrop admin assignment table.');
END
GO

IF EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Authorization.RequireAdminGroupForAdminPortal')
BEGIN
    UPDATE dbo.AppSettings SET SettingValue = 'false', ModifiedDate = SYSUTCDATETIME()
    WHERE SettingKey = 'Authorization.RequireAdminGroupForAdminPortal';
END
GO
