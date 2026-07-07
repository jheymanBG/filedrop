using FileDrop.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
{
    var logPath = context.Configuration["Storage:LogsPath"] ?? "C:\\SecureFileTransfer\\Logs";
    Directory.CreateDirectory(logPath);
    config.WriteTo.File(Path.Combine(logPath, "filedrop-.log"), rollingInterval: RollingInterval.Day);
});

builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = null;
});

builder.Services
    .AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorPages();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IStorageService, StorageService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<ICleanupService, CleanupService>();
builder.Services.AddScoped<ISettingsRepository, SettingsRepository>();
builder.Services.AddScoped<IHealthService, HealthService>();
builder.Services.AddScoped<IUploadPolicyService, UploadPolicyService>();
builder.Services.AddScoped<IAdminAccessService, AdminAccessService>();
builder.Services.AddScoped<IAdminUserRepository, AdminUserRepository>();
builder.Services.AddScoped<IUploadManagerService, UploadManagerService>();
builder.Services.AddScoped<IChunkedUploadService, ChunkedUploadService>();
builder.Services.AddScoped<IDownloadNotificationService, DownloadNotificationService>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IEmailTestService, EmailTestService>();
builder.Services.AddScoped<IBrandingService, BrandingService>();
builder.Services.AddScoped<IEntraReadinessService, EntraReadinessService>();
builder.Services.AddScoped<IProductionRepository, ProductionRepository>();
builder.Services.AddScoped<IVirusScanService, VirusScanService>();
builder.Services.AddScoped<IFileScanRepository, FileScanRepository>();
builder.Services.AddScoped<IEnterpriseAuthorizationService, EnterpriseAuthorizationService>();
builder.Services.AddScoped<IEntraValidationService, EntraValidationService>();
builder.Services.AddHostedService<ChunkedUploadCleanupHostedService>();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = null);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

















