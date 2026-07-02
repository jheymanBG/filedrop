using System.Security.Cryptography;

namespace FileDrop.Web.Services;

public interface IStorageService
{
    Task<(string storagePath, string storedFileName, string sha256)> SaveFileAsync(IFormFile file, Guid transferId, CancellationToken cancellationToken);
}

public sealed class StorageService : IStorageService
{
    private readonly IConfiguration _config;

    public StorageService(IConfiguration config)
    {
        _config = config;
    }

    public async Task<(string storagePath, string storedFileName, string sha256)> SaveFileAsync(IFormFile file, Guid transferId, CancellationToken cancellationToken)
    {
        var filesRoot = _config["Storage:FilesPath"] ?? "C:\\SecureFileTransfer\\Files";
        var folder = Path.Combine(filesRoot, DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), transferId.ToString("N"));
        Directory.CreateDirectory(folder);

        var storedFileName = $"{Guid.NewGuid():N}.bin";
        var storagePath = Path.Combine(folder, storedFileName);

        await using var input = file.OpenReadStream();
        await using var output = File.Create(storagePath);
        using var sha = SHA256.Create();

        var buffer = new byte[1024 * 1024];
        int read;

        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (storagePath, storedFileName, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }
}
