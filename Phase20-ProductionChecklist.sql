IF OBJECT_ID('dbo.ProductionChecklist', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductionChecklist (
        ChecklistId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Category NVARCHAR(100) NOT NULL,
        Item NVARCHAR(300) NOT NULL,
        IsComplete BIT NOT NULL DEFAULT 0,
        Notes NVARCHAR(MAX) NULL,
        SortOrder INT NOT NULL DEFAULT 0,
        ModifiedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM dbo.ProductionChecklist)
BEGIN
    INSERT INTO dbo.ProductionChecklist (Category, Item, SortOrder) VALUES
    ('Identity','Entra app registration created',10),
    ('Identity','Admin consent granted',20),
    ('Identity','Microsoft login required for uploads',30),
    ('Network','Production DNS record created for filedrop.bgohio.gov',40),
    ('Network','HTTPS certificate installed in IIS',50),
    ('Email','SMTP relay tested successfully',60),
    ('Storage','Storage path confirmed and secured',70),
    ('Backup','Backup script tested',80),
    ('Backup','Restore script tested',90),
    ('Security','Admin portal restricted to authorized users',100),
    ('Security','Blocked upload extensions reviewed',110),
    ('Operations','Health page shows database/storage/email OK',120),
    ('Operations','Reports/export tested',130),
    ('Branding','Official City logo uploaded',140),
    ('Go Live','Pilot transfer sent and downloaded externally',150);
END
GO
