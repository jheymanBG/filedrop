namespace FileDrop.Web.Services;

public interface IRuntimeSettings
{
    Task<string> GetStringAsync(string settingKey, string? configurationKey, string defaultValue);
    Task<bool> GetBoolAsync(string settingKey, string? configurationKey, bool defaultValue);
    Task<int> GetIntAsync(string settingKey, string? configurationKey, int defaultValue);
}

/// <summary>
/// Resolves administrator-controlled runtime settings from dbo.AppSettings first,
/// then falls back to appsettings/environment configuration. This keeps the Admin
/// UI, runtime behavior, and Diagnostics on one source of truth while retaining
/// safe file-based defaults when the database value is absent.
/// </summary>
public sealed class RuntimeSettings : IRuntimeSettings
{
    private readonly ISettingsRepository _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<RuntimeSettings> _logger;

    public RuntimeSettings(ISettingsRepository settings, IConfiguration config, ILogger<RuntimeSettings> logger)
    {
        _settings = settings;
        _config = config;
        _logger = logger;
    }

    public async Task<string> GetStringAsync(string settingKey, string? configurationKey, string defaultValue)
    {
        try
        {
            var databaseValue = await _settings.GetValueAsync(settingKey);
            if (!string.IsNullOrWhiteSpace(databaseValue))
            {
                return databaseValue.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read runtime setting {SettingKey} from database. Falling back to configuration.", settingKey);
        }

        var configKey = string.IsNullOrWhiteSpace(configurationKey)
            ? settingKey.Replace('.', ':')
            : configurationKey;
        var configured = _config[configKey];
        return string.IsNullOrWhiteSpace(configured) ? defaultValue : configured.Trim();
    }

    public async Task<bool> GetBoolAsync(string settingKey, string? configurationKey, bool defaultValue)
    {
        var raw = await GetStringAsync(settingKey, configurationKey, defaultValue.ToString());
        if (bool.TryParse(raw, out var parsed)) return parsed;
        if (int.TryParse(raw, out var numeric)) return numeric != 0;
        return defaultValue;
    }

    public async Task<int> GetIntAsync(string settingKey, string? configurationKey, int defaultValue)
    {
        var raw = await GetStringAsync(settingKey, configurationKey, defaultValue.ToString());
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}
