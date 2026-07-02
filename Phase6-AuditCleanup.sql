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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_TransferId')
BEGIN
    CREATE INDEX IX_AuditLog_TransferId ON dbo.AuditLog(TransferId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_CreatedDate')
BEGIN
    CREATE INDEX IX_AuditLog_CreatedDate ON dbo.AuditLog(CreatedDate);
END
GO
