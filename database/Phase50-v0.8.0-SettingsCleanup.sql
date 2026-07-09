/*
Phase 50 - Settings cleanup
This migration removes deprecated settings from dbo.AppSettings that are no longer displayed
or used by the v0.8.0 administration UI. The active settings page now shows only operational
settings for alerts, uploads, transfers, notifications, cleanup, and security scanning.
*/

IF OBJECT_ID('dbo.AppSettings', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.AppSettings
    WHERE SettingKey IN (
        'Security.AllowTemporaryLocalUpload',
        'Security.AutoSwitchToMicrosoftLogin',
        'Security.EntraAdminConsentGranted',
        'Security.LastSuccessfulMicrosoftLogin'
    );
END
GO
