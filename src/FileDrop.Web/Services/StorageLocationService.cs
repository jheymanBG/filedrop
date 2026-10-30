using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IStorageLocationService
{
    Task<StorageAdminViewModel> GetAdminModelAsync();
    Task<StoragePathTestResponse> TestPathAsync(string rootPath, CancellationToken cancellationToken = default);
    Task SaveActiveLocationAsync(string rootPath, bool readOnly, CancellationToken cancellationToken = default);
}

public sealed class StorageLocationService : IStorageLocationService
{
    private readonly ISettingsRepository _settings;
    private readonly IConfiguration _config;
    private readonly IVirusScanService _virusScan;

    public StorageLocationService(ISettingsRepository settings, IConfiguration config, IVirusScanService virusScan)
    {
        _settings = settings;
        _config = config;
        _virusScan = virusScan;
    }

    public async Task<StorageAdminViewModel> GetAdminModelAsync()
    {
        var root = await _settings.GetStringAsync("Storage.RootPath", _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer");
        var active = await BuildLocationAsync(root, true);
        var defenderPath = VirusScanService.FindDefenderExecutable();

        return new StorageAdminViewModel
        {
            ActiveLocation = active,
            Locations = new List<StorageLocationViewModel> { active },
            ProtectionEnabled = await _settings.GetBoolAsync("Storage.Protection.Enabled", true),
            MinimumFreeSpaceBytes = ParseLong(await _settings.GetValueAsync("Storage.MinimumFreeSpaceBytes"), 10L * 1024 * 1024 * 1024),
            SafetyBufferBytes = ParseLong(await _settings.GetValueAsync("Storage.SafetyBufferBytes"), 10L * 1024 * 1024 * 1024),
            ContactDepartment = await _settings.GetStringAsync("Storage.ContactDepartment", "GIS Department"),
            ContactEmail = await _settings.GetStringAsync("Storage.ContactEmail", "gisstaff@bgohio.gov"),
            DefenderAvailable = !string.IsNullOrWhiteSpace(defenderPath),
            DefenderStatus = string.IsNullOrWhiteSpace(defenderPath) ? "Microsoft Defender command-line scanner was not found." : defenderPath,
            DriveAlerts = await BuildDriveAlertsAsync(root)
        };
    }

    public async Task<StoragePathTestResponse> TestPathAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new StoragePathTestResponse { Success = false, Message = "Storage path is required." };
        }

        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootPath.Trim()));
        var result = new StoragePathTestResponse();

        try
        {
            Directory.CreateDirectory(full);
            result.Exists = Directory.Exists(full);

            _ = Directory.EnumerateFileSystemEntries(full).Take(1).ToList();
            result.CanRead = true;

            var testFile = Path.Combine(full, $".filedrop-storage-test-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(testFile, "FileDrop storage validation", cancellationToken);
            result.CanWrite = File.Exists(testFile);
            File.Delete(testFile);
            result.CanDelete = !File.Exists(testFile);

            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            result.TotalBytes = drive.TotalSize;
            result.FreeBytes = drive.AvailableFreeSpace;
            result.FreePercent = drive.TotalSize <= 0 ? 0 : Math.Round((decimal)drive.AvailableFreeSpace / drive.TotalSize * 100m, 1);
            result.Success = result.Exists && result.CanRead && result.CanWrite && result.CanDelete;
            result.Message = result.Success ? "Storage path passed read, write, and delete tests." : "Storage path failed one or more validation tests.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
        }

        return result;
    }

    public async Task SaveActiveLocationAsync(string rootPath, bool readOnly, CancellationToken cancellationToken = default)
    {
        var test = await TestPathAsync(rootPath, cancellationToken);
        if (!test.Success)
        {
            throw new InvalidOperationException(test.Message);
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootPath.Trim())).TrimEnd(Path.DirectorySeparatorChar);
        await _settings.UpsertAsync("Storage.RootPath", root, "Active storage root for new uploads.");
        await _settings.UpsertAsync("Storage.FilesPath", Path.Combine(root, "Files"), "Active final-file storage path for new uploads.");
        await _settings.UpsertAsync("Storage.TempPath", Path.Combine(root, "Temp"), "Active temporary chunk storage path for new uploads.");
        await _settings.UpsertAsync("Storage.QuarantinePath", Path.Combine(root, "Quarantine"), "Active quarantine path for new uploads.");
        await _settings.UpsertAsync("Storage.LogsPath", Path.Combine(root, "Logs"), "Active FileDrop log path.");
        await _settings.UpsertAsync("Storage.ActiveReadOnly", readOnly.ToString(), "Prevent new uploads from using the active storage location.");

        Directory.CreateDirectory(Path.Combine(root, "Files"));
        Directory.CreateDirectory(Path.Combine(root, "Temp"));
        Directory.CreateDirectory(Path.Combine(root, "Quarantine"));
        Directory.CreateDirectory(Path.Combine(root, "Logs"));
    }

    private async Task<StorageLocationViewModel> BuildLocationAsync(string rootPath, bool active)
    {
        var test = await TestPathAsync(rootPath);
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootPath));
        return new StorageLocationViewModel
        {
            Name = "Primary",
            RootPath = root,
            FilesPath = await _settings.GetStringAsync("Storage.FilesPath", Path.Combine(root, "Files")),
            TempPath = await _settings.GetStringAsync("Storage.TempPath", Path.Combine(root, "Temp")),
            QuarantinePath = await _settings.GetStringAsync("Storage.QuarantinePath", Path.Combine(root, "Quarantine")),
            LogsPath = await _settings.GetStringAsync("Storage.LogsPath", Path.Combine(root, "Logs")),
            IsActive = active,
            IsReadOnly = await _settings.GetBoolAsync("Storage.ActiveReadOnly", false),
            Exists = test.Exists,
            CanRead = test.CanRead,
            CanWrite = test.CanWrite,
            CanDelete = test.CanDelete,
            TotalBytes = test.TotalBytes,
            FreeBytes = test.FreeBytes,
            FreePercent = test.FreePercent,
            ValidationMessage = test.Message
        };
    }

    private async Task<List<StorageDriveAlertViewModel>> BuildDriveAlertsAsync(string activeRoot)
    {
        var result = new List<StorageDriveAlertViewModel>();
        var activeDrive = NormalizeDriveName(Path.GetPathRoot(Path.GetFullPath(Environment.ExpandEnvironmentVariables(activeRoot))) ?? string.Empty);

        foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
        {
            if (drive.DriveType is not DriveType.Fixed and not DriveType.Network)
            {
                continue;
            }

            var driveName = NormalizeDriveName(drive.Name);
            var isActive = string.Equals(driveName, activeDrive, StringComparison.OrdinalIgnoreCase);
            var enabled = await _settings.GetBoolAsync(DriveAlertSettingKey(driveName), isActive);

            var vm = new StorageDriveAlertViewModel
            {
                DriveName = driveName,
                DriveType = drive.DriveType.ToString(),
                IsReady = drive.IsReady,
                AlertsEnabled = enabled,
                IsActiveStorageDrive = isActive
            };

            if (drive.IsReady)
            {
                vm.VolumeLabel = drive.VolumeLabel;
                vm.TotalBytes = drive.TotalSize;
                vm.FreeBytes = drive.AvailableFreeSpace;
                vm.FreePercent = drive.TotalSize <= 0 ? 0 : Math.Round((decimal)drive.AvailableFreeSpace / drive.TotalSize * 100m, 1);
            }

            result.Add(vm);
        }

        return result;
    }

    public static string DriveAlertSettingKey(string driveName)
        => $"Storage.DriveAlerts.{NormalizeDriveName(driveName).TrimEnd(':')}.Enabled";

    public static string NormalizeDriveName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var root = Path.GetPathRoot(value) ?? value;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }

    private static long ParseLong(string? value, long fallback) => long.TryParse(value, out var parsed) ? parsed : fallback;
}
