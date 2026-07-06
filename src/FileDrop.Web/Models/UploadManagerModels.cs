namespace FileDrop.Web.Models;

public sealed class UploadManagerViewModel
{
    public long MaximumUploadBytes { get; set; }
    public decimal MaximumUploadGB { get; set; }
    public int MaximumFilesPerTransfer { get; set; }
    public int RequestTimeoutMinutes { get; set; }
    public long EffectiveWebConfigBytes { get; set; }
    public string PublishWebConfigPath { get; set; } = "";
    public bool PublishWebConfigExists { get; set; }
    public bool IsSynced => EffectiveWebConfigBytes == MaximumUploadBytes;
}
