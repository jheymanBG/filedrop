using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IStorageProtectionService
{
    Task<UploadPreflightResponse> CheckAsync(UploadPreflightRequest request, CancellationToken cancellationToken = default);
    Task<string> GetActiveRootPathAsync();
    Task<string> GetActiveTempPathAsync();
    Task<string> GetActiveFilesPathAsync();
    Task<string> GetActiveQuarantinePathAsync();
    Task<string> GetActiveLogsPathAsync();
}

public sealed class StorageProtectionService : IStorageProtectionService
{
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settings;

    public StorageProtectionService(IConfiguration config, ISettingsRepository settings)
    {
        _config = config;
        _settings = settings;
    }

    public async Task<UploadPreflightResponse> CheckAsync(UploadPreflightRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UploadPreflightRequest();
        var files = request.Files ?? new List<UploadPreflightFileItem>();
        var root = await GetActiveRootPathAsync();
        var contactEmail = await GetSettingAsync("Storage.ContactEmail", "gisstaff@bgohio.gov");
        var contactDepartment = await GetSettingAsync("Storage.ContactDepartment", "GIS Department");
        var protectionEnabled = await GetBoolAsync("Storage.Protection.Enabled", true);
        var activeReadOnly = await GetBoolAsync("Storage.ActiveReadOnly", false);
        var safetyBufferBytes = await GetLongAsync("Storage.SafetyBufferBytes", 10L * 1024L * 1024L * 1024L);
        var minimumFreeBytes = await GetLongAsync("Storage.MinimumFreeSpaceBytes", 10L * 1024L * 1024L * 1024L);
        var blockedExtensionsText = await GetSettingAsync("Uploads.BlockedExtensions", ".exe,.msi,.bat,.cmd,.ps1,.vbs,.js,.jar,.com,.scr,.dll");
        var blockedExtensions = blockedExtensionsText.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var totalSelected = files.Sum(f => Math.Max(0, f.TotalBytes));
        var available = GetAvailableBytes(root);
        var required = totalSelected + safetyBufferBytes + minimumFreeBytes;

        var response = new UploadPreflightResponse
        {
            Allowed = true,
            AcceptingUploads = true,
            Title = "Upload Check Passed",
            Message = "Files are ready to upload.",
            ContactDepartment = contactDepartment,
            ContactEmail = contactEmail,
            TotalSelectedBytes = totalSelected,
            AvailableBytes = available,
            RequiredBytes = required,
            StorageRoot = root
        };

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName ?? string.Empty);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var fileResult = new UploadPreflightFileResult
            {
                FileName = name,
                TotalBytes = file.TotalBytes,
                Allowed = true,
                Status = "Allowed",
                Message = "Allowed"
            };

            if (!string.IsNullOrWhiteSpace(ext) && blockedExtensions.Contains(ext))
            {
                fileResult.Allowed = false;
                fileResult.Status = "Blocked";
                fileResult.Message = $"The file type {ext} is not allowed.";
                response.Allowed = false;
                response.AcceptingUploads = false;
                response.Title = "File Type Not Allowed";
                response.Message = $"One or more selected files use a file type that is not allowed. Please remove blocked files before uploading.";
            }

            response.Files.Add(fileResult);
        }

        if (activeReadOnly)
        {
            response.Allowed = false;
            response.AcceptingUploads = false;
            response.Title = "Uploads Temporarily Unavailable";
            response.Message = $"The FileDrop server is not accepting uploads at this time. Please contact the {contactDepartment}.";
        }
        else if (protectionEnabled && available > 0 && available < required)
        {
            response.Allowed = false;
            response.AcceptingUploads = false;
            response.Title = "Uploads Temporarily Unavailable";
            response.Message = $"The FileDrop server is not accepting uploads at this time because available storage is below the required threshold. Please contact the {contactDepartment}.";
        }

        return response;
    }

    public async Task<string> GetActiveRootPathAsync() => await GetSettingAsync("Storage.RootPath", _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer");
    public async Task<string> GetActiveTempPathAsync() => await GetSettingAsync("Storage.TempPath", _config["Storage:TempPath"] ?? Path.Combine(await GetActiveRootPathAsync(), "Temp"));
    public async Task<string> GetActiveFilesPathAsync() => await GetSettingAsync("Storage.FilesPath", _config["Storage:FilesPath"] ?? Path.Combine(await GetActiveRootPathAsync(), "Files"));
    public async Task<string> GetActiveQuarantinePathAsync() => await GetSettingAsync("Storage.QuarantinePath", _config["Storage:QuarantinePath"] ?? Path.Combine(await GetActiveRootPathAsync(), "Quarantine"));
    public async Task<string> GetActiveLogsPathAsync() => await GetSettingAsync("Storage.LogsPath", _config["Storage:LogsPath"] ?? Path.Combine(await GetActiveRootPathAsync(), "Logs"));

    private async Task<string> GetSettingAsync(string key, string defaultValue)
    {
        var value = await _settings.GetValueAsync(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private async Task<bool> GetBoolAsync(string key, bool defaultValue)
    {
        var value = await _settings.GetValueAsync(key);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private async Task<long> GetLongAsync(string key, long defaultValue)
    {
        var value = await _settings.GetValueAsync(key);
        return long.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static long GetAvailableBytes(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            var existing = Directory.Exists(full) ? full : Directory.GetDirectoryRoot(full);
            var drive = new DriveInfo(Path.GetPathRoot(existing)!);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
