IF DB_ID('FileDrop') IS NULL
BEGIN
    CREATE DATABASE [FileDrop];
END
GO

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = 'filedrop')
BEGIN
    CREATE LOGIN [filedrop]
    WITH PASSWORD = 'Passwords are dumb!',
    CHECK_POLICY = ON,
    CHECK_EXPIRATION = OFF;
END
GO

USE [FileDrop];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'filedrop')
BEGIN
    CREATE USER [filedrop] FOR LOGIN [filedrop];
END
GO

ALTER ROLE db_datareader ADD MEMBER [filedrop];
ALTER ROLE db_datawriter ADD MEMBER [filedrop];
GO

IF OBJECT_ID('dbo.Transfers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Transfers (
        TransferId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        SenderEmail NVARCHAR(255) NOT NULL,
        SenderName NVARCHAR(255) NULL,
        RecipientEmail NVARCHAR(255) NOT NULL,
        Subject NVARCHAR(255) NULL,
        Message NVARCHAR(MAX) NULL,
        DownloadToken NVARCHAR(128) NOT NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ExpirationDate DATETIME2 NOT NULL,
        FirstDownloadDate DATETIME2 NULL,
        LastDownloadDate DATETIME2 NULL,
        DownloadCount INT NOT NULL DEFAULT 0,
        Status NVARCHAR(50) NOT NULL DEFAULT 'Active'
    );
END
GO

IF OBJECT_ID('dbo.TransferFiles', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TransferFiles (
        FileId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NOT NULL,
        OriginalFileName NVARCHAR(512) NOT NULL,
        StoredFileName NVARCHAR(512) NOT NULL,
        StoragePath NVARCHAR(1000) NOT NULL,
        ContentType NVARCHAR(255) NULL,
        FileSizeBytes BIGINT NOT NULL,
        Sha256Hash NVARCHAR(128) NULL,
        UploadedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ScanStatus NVARCHAR(50) NULL,
        CONSTRAINT FK_TransferFiles_Transfers
            FOREIGN KEY (TransferId) REFERENCES dbo.Transfers(TransferId)
    );
END
GO

IF OBJECT_ID('dbo.AuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditLog (
        AuditId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NULL,
        UserEmail NVARCHAR(255) NULL,
        Action NVARCHAR(100) NOT NULL,
        Details NVARCHAR(MAX) NULL,
        IpAddress NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Transfers_DownloadToken')
BEGIN
    CREATE INDEX IX_Transfers_DownloadToken ON dbo.Transfers(DownloadToken);
END
GO
