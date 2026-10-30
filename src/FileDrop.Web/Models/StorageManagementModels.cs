namespace FileDrop.Web.Models;

public sealed class StorageLocationViewModel
{
    public string Name { get; set; } = "Primary";
    public string RootPath { get; set; } = "";
    public string FilesPath { get; set; } = "";
    public string TempPath { get; set; } = "";
    public string QuarantinePath { get; set; } = "";
    public string LogsPath { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsReadOnly { get; set; }
    public bool Exists { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public decimal FreePercent { get; set; }
    public string? ValidationMessage { get; set; }
}


public sealed class StorageDriveAlertViewModel
{
    public string DriveName { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string DriveType { get; set; } = "";
    public bool IsReady { get; set; }
    public bool AlertsEnabled { get; set; }
    public bool IsActiveStorageDrive { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public decimal FreePercent { get; set; }
}

public sealed class StorageAdminViewModel
{
    public StorageLocationViewModel ActiveLocation { get; set; } = new();
    public List<StorageLocationViewModel> Locations { get; set; } = new();
    public bool ProtectionEnabled { get; set; } = true;
    public long MinimumFreeSpaceBytes { get; set; }
    public long SafetyBufferBytes { get; set; }
    public string ContactDepartment { get; set; } = "GIS Department";
    public string ContactEmail { get; set; } = "gisstaff@bgohio.gov";
    public bool DefenderAvailable { get; set; }
    public string DefenderStatus { get; set; } = "Unknown";
    public List<StorageDriveAlertViewModel> DriveAlerts { get; set; } = new();
}

public sealed class StoragePathTestRequest
{
    public string RootPath { get; set; } = "";
}

public sealed class StoragePathTestResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool Exists { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public decimal FreePercent { get; set; }
}
