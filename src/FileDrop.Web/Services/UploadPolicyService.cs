namespace FileDrop.Web.Services;

public sealed class UploadPolicyResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
}

public interface IUploadPolicyService
{
    Task<UploadPolicyResult> ValidateAsync(IReadOnlyList<IFormFile> files, string? subject);
}

public sealed class UploadPolicyService : IUploadPolicyService
{
    private readonly ISettingsRepository _settings;

    public UploadPolicyService(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<UploadPolicyResult> ValidateAsync(IReadOnlyList<IFormFile> files, string? subject)
    {
        var result = new UploadPolicyResult();

        var maxTotalMb = await _settings.GetIntAsync("Uploads.MaxTotalUploadMB", 20480);
        var maxSingleMb = await _settings.GetIntAsync("Uploads.MaxSingleFileMB", 10240);
        var requireSubject = (await _settings.GetValueAsync("Uploads.RequireSubject"))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var blockedSetting = await _settings.GetValueAsync("Uploads.BlockedExtensions") ?? "";

        var blocked = blockedSetting
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToHashSet();

        if (requireSubject && string.IsNullOrWhiteSpace(subject))
        {
            result.Errors.Add("Subject is required.");
        }

        var totalBytes = files.Sum(f => f.Length);
        var maxTotalBytes = maxTotalMb * 1024L * 1024L;
        var maxSingleBytes = maxSingleMb * 1024L * 1024L;

        if (totalBytes > maxTotalBytes)
        {
            result.Errors.Add($"Total upload size exceeds the configured limit of {maxTotalMb:N0} MB.");
        }

        foreach (var file in files)
        {
            if (file.Length > maxSingleBytes)
            {
                result.Errors.Add($"{file.FileName} exceeds the single-file limit of {maxSingleMb:N0} MB.");
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext) && blocked.Contains(ext))
            {
                result.Errors.Add($"{file.FileName} is blocked because {ext} files are not allowed.");
            }

            if (string.IsNullOrWhiteSpace(file.FileName))
            {
                result.Errors.Add("One uploaded file has an invalid filename.");
            }
        }

        return result;
    }
}
