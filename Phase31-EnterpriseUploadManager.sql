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
('Upload.MaximumUploadBytes','107374182400','Maximum total upload request size in bytes. 100 GB default.'),
('Upload.MaximumUploadGB','100','Maximum total upload request size in GB.'),
('Upload.MaximumFilesPerTransfer','500','Maximum number of files allowed per transfer.'),
('Upload.RequestTimeoutMinutes','1440','Large upload request timeout in minutes.')
) AS source (SettingKey, SettingValue, Description)
ON target.SettingKey = source.SettingKey
WHEN MATCHED THEN UPDATE SET SettingValue = source.SettingValue, Description = source.Description, ModifiedDate = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, Description) VALUES (source.SettingKey, source.SettingValue, source.Description);
GO
