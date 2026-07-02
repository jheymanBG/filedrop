using FileDrop.Web.Models;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IHealthService
{
    Task<HealthCheckViewModel> CheckAsync();
}

public sealed class HealthService : IHealthService
{
    private readonly IConfiguration _config;

    public HealthService(IConfiguration config)
    {
        _config = config;
    }

    public async Task<HealthCheckViewModel> CheckAsync()
    {
        var model = new HealthCheckViewModel
        {
            StorageRoot = _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer",
            CheckedAt = DateTime.Now
        };

        try
        {
            var cs = _config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
            await using var db = new SqlConnection(cs);
            await db.OpenAsync();
            model.DatabaseOk = true;
            model.DatabaseMessage = "Connected";
        }
        catch (Exception ex)
        {
            model.DatabaseOk = false;
            model.DatabaseMessage = ex.Message;
        }

        try
        {
            Directory.CreateDirectory(model.StorageRoot);
            var testPath = Path.Combine(model.StorageRoot, "healthcheck.tmp");
            await File.WriteAllTextAsync(testPath, DateTime.UtcNow.ToString("O"));
            File.Delete(testPath);

            model.StorageOk = true;
            model.StorageMessage = "Read/write OK";
            model.StorageBytes = GetDirectorySize(model.StorageRoot);
        }
        catch (Exception ex)
        {
            model.StorageOk = false;
            model.StorageMessage = ex.Message;
        }

        var smtp = _config["Email:SmtpServer"];
        var from = _config["Email:FromEmail"];
        model.EmailConfigured = !string.IsNullOrWhiteSpace(smtp) && !string.IsNullOrWhiteSpace(from);
        model.EmailMessage = model.EmailConfigured ? $"{smtp} / {from}" : "SMTP not configured";

        return model;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // ignore locked/inaccessible files
            }
        }

        return total;
    }
}
