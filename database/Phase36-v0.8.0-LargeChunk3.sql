/*
    FileDrop Enterprise v0.8.0 Large Chunk 3
    Live Upload Manager dashboard and cleanup settings.

    Idempotent migration. Safe to run more than once.
*/

IF OBJECT_ID('dbo.ChunkedUploadSessions', 'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = 'IX_ChunkedUploadSessions_Dashboard'
          AND object_id = OBJECT_ID('dbo.ChunkedUploadSessions')
    )
    BEGIN
        CREATE INDEX IX_ChunkedUploadSessions_Dashboard
        ON dbo.ChunkedUploadSessions (Status, LastActivityDate DESC)
        INCLUDE (UploadId, OriginalFileName, CreatedByEmail, ChunksReceived, TotalChunks, BytesReceived, TotalBytes, CreatedDate, CompletedDate, Sha256Hash);
    END;
END;
GO

IF OBJECT_ID('dbo.AppSettings', 'U') IS NOT NULL
BEGIN
    MERGE dbo.AppSettings AS target
    USING (VALUES
        ('Upload.LiveDashboardRefreshSeconds','15','Admin Upload Manager live dashboard refresh interval.'),
        ('Upload.AbandonedCleanupDefaultHours','24','Default stale upload age used by admin cleanup.'),
        ('Upload.DashboardRecentRows','25','Number of active and recent upload rows shown on Upload Manager dashboard.')
    ) AS source (SettingKey, SettingValue, Description)
    ON target.SettingKey = source.SettingKey
    WHEN MATCHED THEN UPDATE SET SettingValue = source.SettingValue, Description = source.Description, ModifiedDate = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES (source.SettingKey, source.SettingValue, source.Description);
END;
GO
