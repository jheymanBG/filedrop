IF OBJECT_ID('dbo.DownloadNotificationLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DownloadNotificationLog (
        DownloadNotificationId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TransferId UNIQUEIDENTIFIER NOT NULL,
        SentToEmail NVARCHAR(255) NOT NULL,
        RecipientEmail NVARCHAR(255) NULL,
        FileName NVARCHAR(500) NULL,
        SentDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        Status NVARCHAR(50) NOT NULL DEFAULT 'Sent',
        Details NVARCHAR(MAX) NULL
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

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Notifications.SendDownloadConfirmationToSender')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Notifications.SendDownloadConfirmationToSender', 'true', 'Email the sender when the recipient downloads a file.');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE SettingKey = 'Notifications.DownloadConfirmationSubject')
BEGIN
    INSERT INTO dbo.AppSettings (SettingKey, SettingValue, Description)
    VALUES ('Notifications.DownloadConfirmationSubject', 'FileDrop download confirmation', 'Subject for download confirmation emails sent to senders.');
END
GO
