namespace FileDrop.Web.Models;

public sealed class FileDropAdminUser
{
    public int AdminUserId { get; set; }
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public string RoleName { get; set; } = "Administrator";
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime ModifiedDate { get; set; }
}

public sealed class AdminUsersViewModel
{
    public List<FileDropAdminUser> Users { get; set; } = new();
}
