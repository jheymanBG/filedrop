using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FileDrop.Web.Services;

public interface IReportRepository
{
    Task<string> ExportTransfersCsvAsync();
    Task<string> ExportFilesCsvAsync();
    Task<string> ExportAuditCsvAsync();
    Task<string> ExportStorageSummaryCsvAsync();
}

public sealed class ReportRepository : IReportRepository
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;

    public ReportRepository(IConfiguration config)
    {
        _config = config;
        _connectionString = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing.");
    }

    public async Task<string> ExportTransfersCsvAsync()
    {
        await using var db = new SqlConnection(_connectionString);

        var rows = await db.QueryAsync("""
            SELECT
                t.TransferId,
                t.SenderEmail,
                t.SenderName,
                t.RecipientEmail,
                t.Subject,
                t.CreatedDate,
                t.ExpirationDate,
                t.FirstDownloadDate,
                t.LastDownloadDate,
                t.DownloadCount,
                t.Status,
                COUNT(f.FileId) AS FileCount,
                COALESCE(SUM(f.FileSizeBytes),0) AS TotalBytes
            FROM dbo.Transfers t
            LEFT JOIN dbo.TransferFiles f ON f.TransferId = t.TransferId
            GROUP BY
                t.TransferId, t.SenderEmail, t.SenderName, t.RecipientEmail, t.Subject,
                t.CreatedDate, t.ExpirationDate, t.FirstDownloadDate, t.LastDownloadDate,
                t.DownloadCount, t.Status
            ORDER BY t.CreatedDate DESC
            """);

        return ToCsv(rows);
    }

    public async Task<string> ExportFilesCsvAsync()
    {
        await using var db = new SqlConnection(_connectionString);

        var rows = await db.QueryAsync("""
            SELECT
                f.FileId,
                f.TransferId,
                f.OriginalFileName,
                f.StoredFileName,
                f.StoragePath,
                f.ContentType,
                f.FileSizeBytes,
                f.Sha256Hash,
                f.UploadedDate,
                t.SenderEmail,
                t.RecipientEmail,
                t.Subject,
                t.Status
            FROM dbo.TransferFiles f
            INNER JOIN dbo.Transfers t ON t.TransferId = f.TransferId
            ORDER BY f.UploadedDate DESC
            """);

        return ToCsv(rows);
    }

    public async Task<string> ExportAuditCsvAsync()
    {
        await using var db = new SqlConnection(_connectionString);

        var rows = await db.QueryAsync("""
            SELECT TOP 50000
                AuditId,
                TransferId,
                UserEmail,
                Action,
                Details,
                IpAddress,
                CreatedDate
            FROM dbo.AuditLog
            ORDER BY CreatedDate DESC
            """);

        return ToCsv(rows);
    }

    public async Task<string> ExportStorageSummaryCsvAsync()
    {
        var root = _config["Storage:RootPath"] ?? "C:\\SecureFileTransfer";
        var filesRoot = _config["Storage:FilesPath"] ?? Path.Combine(root, "Files");

        var sb = new StringBuilder();
        sb.AppendLine("Category,Value");

        Append(sb, "StorageRoot", root);
        Append(sb, "FilesPath", filesRoot);
        Append(sb, "GeneratedAt", DateTime.Now.ToString("s"));

        if (Directory.Exists(root))
        {
            Append(sb, "RootBytes", GetDirectorySize(root).ToString());
        }

        if (Directory.Exists(filesRoot))
        {
            Append(sb, "FilesBytes", GetDirectorySize(filesRoot).ToString());
            Append(sb, "FileCount", Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories).Count().ToString());
            Append(sb, "FolderCount", Directory.EnumerateDirectories(filesRoot, "*", SearchOption.AllDirectories).Count().ToString());
        }

        await using var db = new SqlConnection(_connectionString);

        var activeBytes = await db.ExecuteScalarAsync<long>("""
            SELECT COALESCE(SUM(f.FileSizeBytes),0)
            FROM dbo.TransferFiles f
            INNER JOIN dbo.Transfers t ON t.TransferId = f.TransferId
            WHERE t.Status = 'Active' AND t.ExpirationDate >= SYSUTCDATETIME()
            """);

        var expiredBytes = await db.ExecuteScalarAsync<long>("""
            SELECT COALESCE(SUM(f.FileSizeBytes),0)
            FROM dbo.TransferFiles f
            INNER JOIN dbo.Transfers t ON t.TransferId = f.TransferId
            WHERE t.ExpirationDate < SYSUTCDATETIME()
            """);

        Append(sb, "ActiveTransferBytes", activeBytes.ToString());
        Append(sb, "ExpiredTransferBytes", expiredBytes.ToString());

        return sb.ToString();
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        sb.Append(Csv(key)).Append(',').Append(Csv(value)).AppendLine();
    }

    private static string ToCsv(IEnumerable<dynamic> rows)
    {
        var list = rows.ToList();
        var sb = new StringBuilder();

        if (list.Count == 0)
        {
            return "";
        }

        var first = (IDictionary<string, object>)list[0];

        sb.AppendLine(string.Join(",", first.Keys.Select(Csv)));

        foreach (IDictionary<string, object> row in list)
        {
            sb.AppendLine(string.Join(",", row.Values.Select(v => Csv(v?.ToString() ?? ""))));
        }

        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
