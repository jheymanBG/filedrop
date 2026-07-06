using System.Xml.Linq;
using FileDrop.Web.Models;

namespace FileDrop.Web.Services;

public interface IUploadManagerService
{
    Task<UploadManagerViewModel> GetAsync();
    Task SaveAsync(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes);
    Task RepairAsync();
}

public sealed class UploadManagerService : IUploadManagerService
{
    private readonly ISettingsRepository _settings;

    public UploadManagerService(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task<UploadManagerViewModel> GetAsync()
    {
        var bytes = await GetConfiguredBytesAsync();
        var webConfig = GetPublishWebConfigPath();

        return new UploadManagerViewModel
        {
            MaximumUploadBytes = bytes,
            MaximumUploadGB = Math.Round(bytes / 1024m / 1024m / 1024m, 2),
            MaximumFilesPerTransfer = await _settings.GetIntAsync("Upload.MaximumFilesPerTransfer", 500),
            RequestTimeoutMinutes = await _settings.GetIntAsync("Upload.RequestTimeoutMinutes", 1440),
            PublishWebConfigPath = webConfig,
            PublishWebConfigExists = File.Exists(webConfig),
            EffectiveWebConfigBytes = File.Exists(webConfig) ? ReadWebConfigLimit(webConfig) : 0
        };
    }

    public async Task SaveAsync(decimal maximumUploadGb, int maximumFilesPerTransfer, int requestTimeoutMinutes)
    {
        if (maximumUploadGb < 1 || maximumUploadGb > 500)
        {
            throw new InvalidOperationException("Maximum upload size must be between 1 GB and 500 GB.");
        }

        var bytes = (long)(maximumUploadGb * 1024m * 1024m * 1024m);

        await _settings.UpdateAsync("Upload.MaximumUploadGB", maximumUploadGb.ToString("0.##"));
        await _settings.UpdateAsync("Upload.MaximumUploadBytes", bytes.ToString());
        await _settings.UpdateAsync("Upload.MaximumFilesPerTransfer", maximumFilesPerTransfer.ToString());
        await _settings.UpdateAsync("Upload.RequestTimeoutMinutes", requestTimeoutMinutes.ToString());

        WriteWebConfigLimit(GetPublishWebConfigPath(), bytes, requestTimeoutMinutes);
    }

    public async Task RepairAsync()
    {
        var bytes = await GetConfiguredBytesAsync();
        var timeout = await _settings.GetIntAsync("Upload.RequestTimeoutMinutes", 1440);
        WriteWebConfigLimit(GetPublishWebConfigPath(), bytes, timeout);
    }

    private async Task<long> GetConfiguredBytesAsync()
    {
        var text = await _settings.GetValueAsync("Upload.MaximumUploadBytes");
        return long.TryParse(text, out var bytes) && bytes > 0 ? bytes : 107374182400;
    }

    private static string GetPublishWebConfigPath() => Path.Combine("C:\\Build\\FileDrop_v1_starter\\Publish", "web.config");

    private static long ReadWebConfigLimit(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var attr = doc.Descendants("requestLimits").FirstOrDefault()?.Attribute("maxAllowedContentLength")?.Value;
            return long.TryParse(attr, out var value) ? value : 0;
        }
        catch { return 0; }
    }

    private static void WriteWebConfigLimit(string path, long bytes, int timeoutMinutes)
    {
        if (!File.Exists(path)) return;

        var doc = XDocument.Load(path);
        var config = doc.Element("configuration")!;
        var sws = config.Element("system.webServer") ?? new XElement("system.webServer");
        if (sws.Parent is null) config.Add(sws);

        var security = sws.Element("security") ?? new XElement("security");
        if (security.Parent is null) sws.Add(security);

        var filtering = security.Element("requestFiltering") ?? new XElement("requestFiltering");
        if (filtering.Parent is null) security.Add(filtering);

        var limits = filtering.Element("requestLimits") ?? new XElement("requestLimits");
        if (limits.Parent is null) filtering.Add(limits);

        limits.SetAttributeValue("maxAllowedContentLength", bytes.ToString());

        var aspNetCore = sws.Element("aspNetCore");
        if (aspNetCore is not null)
        {
            aspNetCore.SetAttributeValue("requestTimeout", $"{timeoutMinutes / 60:D2}:{timeoutMinutes % 60:D2}:00");
        }

        doc.Save(path);
    }
}
