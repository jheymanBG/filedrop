namespace FileDrop.Web.Models;

public sealed class BrandingViewModel
{
    public string OrganizationName { get; set; } = "City of Bowling Green";
    public string ApplicationTitle { get; set; } = "Secure File Transfer";
    public string DepartmentName { get; set; } = "GIS Department";
    public string? LogoPath { get; set; }
}
