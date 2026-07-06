IF OBJECT_ID('dbo.ChunkedUploadSessions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ChunkedUploadSessions (
        UploadId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        CreatedByEmail NVARCHAR(255) NULL,
        OriginalFileName NVARCHAR(500) NOT NULL,
        ContentType NVARCHAR(255) NULL,
        TotalBytes BIGINT NOT NULL,
        ChunkSizeBytes INT NOT NULL,
        TotalChunks INT NOT NULL,
        ChunksReceived INT NOT NULL DEFAULT 0,
        BytesReceived BIGINT NOT NULL DEFAULT 0,
        TempFolder NVARCHAR(1000) NOT NULL,
        FinalStoragePath NVARCHAR(1000) NULL,
        StoredFileName NVARCHAR(500) NULL,
        Sha256Hash NVARCHAR(128) NULL,
        Status NVARCHAR(50) NOT NULL DEFAULT 'Uploading',
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CompletedDate DATETIME2 NULL
    );
END
GO

IF OBJECT_ID('dbo.ChunkedUploadChunks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ChunkedUploadChunks (
        UploadId UNIQUEIDENTIFIER NOT NULL,
        ChunkIndex INT NOT NULL,
        BytesReceived INT NOT NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ChunkedUploadChunks PRIMARY KEY (UploadId, ChunkIndex)
    );
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

MERGE dbo.AppSettings AS target
USING (VALUES
('Upload.ChunkSizeMB','10','Chunk size used by browser upload progress UI.'),
('Upload.EnableChunkedUploads','true','Use chunked upload pipeline for large uploads.'),
('Upload.MaximumUploadBytes','107374182400','Maximum total upload size in bytes. 100 GB default.'),
('Upload.MaximumUploadGB','100','Maximum total upload size in GB.')
) AS source (SettingKey, SettingValue, Description)
ON target.SettingKey = source.SettingKey
WHEN MATCHED THEN UPDATE SET SettingValue = source.SettingValue, Description = source.Description, ModifiedDate = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES (source.SettingKey, source.SettingValue, source.Description);
GO
