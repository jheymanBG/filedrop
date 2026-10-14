# TestDrop (testdrop.bgohio.gov)

Dedicated IIS site + application pool FileDrop-Test, physical directory C:\Sites\TestDrop, SQL database FileDrop_Test, storage C:\SecureFileTransfer-Test.

IMPORTANT: GitHub main is an older snapshot, NOT the confirmed Phase 72 production source. Obtain and verify the latest build before deploying. Never publish main assuming it reflects current production.

## Prerequisites
1. Create testdrop.bgohio.gov DNS and valid SNI-capable HTTPS certificate.
2. Create separate Entra app registration, restrict test users, add redirect https://testdrop.bgohio.gov/signin-oidc.
3. Create FileDrop_Test SQL database and separate least-privilege SQL login. Apply schema/migrations ONLY in TestDrop.
4. Stage a verified publish at C:\Build\TestDrop\Publish (no production secrets).
5. Create a host-local appsettings.TestDrop.json under C:\Build\TestDrop containing fully isolated Application.HostName, Storage.RootPath/FilesPath/TempPath/QuarantinePath/LogsPath, ConnectionStrings.DefaultConnection (Database=FileDrop_Test), AzureAd.ClientId/TenantId and other required identity settings. Set Notifications.SendDownloadNotification=false and Notifications.SendSenderConfirmation=false. DO NOT COMMIT SECRETS.
6. Restrict ingress to approved testers; use synthetic data, not production PII.

## Install
Run elevated PowerShell with WhatIf first, then repeat without WhatIf after verifying:

    .\DeployTools\Setup-TestDrop.ps1 -PublishedSource 'C:\Build\TestDrop\Publish' -TestSettingsPath 'C:\Build\TestDrop\appsettings.TestDrop.json' -CertificateThumbprint 'CERT_THUMBPRINT' -WhatIf

Verify application health, login, independent database/storage, disabled email, PII override, audit, and unchanged production URL. This script does not remotely install anything or provision DNS/Entra/SQL automatically. No production code, site, pool, or database is modified by design.
