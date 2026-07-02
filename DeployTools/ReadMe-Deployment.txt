FileDrop Deployment Tools

Backup:
  C:\Build\FileDrop_v1_starter\DeployTools\Backup-FileDrop.ps1

List backups:
  C:\Build\FileDrop_v1_starter\DeployTools\List-FileDrop-Backups.ps1

Restore publish folder only:
  C:\Build\FileDrop_v1_starter\DeployTools\Restore-FileDrop.ps1 -BackupFolder "C:\Build\FileDrop_v1_starter\Backups\YYYYMMDD-HHMMSS"

Restore publish folder and database:
  C:\Build\FileDrop_v1_starter\DeployTools\Restore-FileDrop.ps1 -BackupFolder "C:\Build\FileDrop_v1_starter\Backups\YYYYMMDD-HHMMSS" -RestoreDatabase

Safe deploy:
  C:\Build\FileDrop_v1_starter\DeployTools\Deploy-FileDrop-Safe.ps1
