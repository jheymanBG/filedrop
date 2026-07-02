IF DB_ID('FileDrop') IS NULL
BEGIN
    CREATE DATABASE [FileDrop];
END
GO

USE [FileDrop];
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
        CONSTRAINT FK_TransferFiles_Transfers FOREIGN KEY (TransferId) REFERENCES dbo.Transfers(TransferId)
    );
END
GO
