using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Blossom;
using Netstr.Data;
using Netstr.Extensions;
using Netstr.Middleware;
using Netstr.Options;
using Netstr.RelayInformation;
using Serilog;

using Netstr.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel network binding (Host IP & Port)
var serverSection = builder.Configuration.GetSection("Server");
var host = serverSection["Host"] ?? "0.0.0.0";
var portStr = serverSection["Port"] ?? "8083";
if (!int.TryParse(portStr, out var port))
{
    port = 8083;
}

var envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(envUrls) && !args.Any(a => a.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase)))
{
    builder.WebHost.UseUrls($"http://{host}:{port}");
}

var connectionString = builder.Configuration.GetConnectionString("NetstrDatabase");

// Setup Serilog logging
builder.Host.UseSerilog((hostingContext, loggerConfiguration) => loggerConfiguration.ReadFrom.Configuration(hostingContext.Configuration));

builder.Services
    .AddCors(x => x.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()))
    .AddControllersWithViews().Services
    .AddHttpContextAccessor()
    .AddApplicationsOptions()
    .AddMessaging()
    .AddHostedService<UserCacheStartupService>()
    .AddHostedService<ModerationStartupService>()
    .AddHostedService<NegentropyBackgroundWatcher>()
    .AddHostedService<CleanupBackgroundService>()
    .AddHostedService<VanishExecutionService>()
    .AddScoped<IRelayInformationService, RelayInformationService>()
    .AddSingleton<IAdminAuthService, AdminAuthService>()
    .AddSingleton<ITelemetryService, TelemetryService>()
    .AddScoped<IModerationService, ModerationService>()
    .AddSingleton<BlossomTokenValidator>()
    .AddSingleton<IBlobStorageService, FileBlobStorageService>()
    .AddSingleton<IBlossomManagerService, BlossomManagerService>()
    .AddDbContextFactory<NetstrDbContext>(x => x.UseNpgsql(connectionString));

var application = builder.Build();

// Setup pipeline + init DB
application
    .UseCors()
    .UseWebSockets()
    .UseDefaultFiles()
    .UseStaticFiles()
    .UseRouting()
    .AcceptWebSocketsConnections()
    .EnsureDbContextMigrations<NetstrDbContext>();

// CLI administrative commands
if (args.Length > 0 && args.Contains("--set-admin-user"))
{
    var userIdx = Array.IndexOf(args, "--set-admin-user") + 1;
    var passIdx = Array.IndexOf(args, "--set-admin-pass") + 1;
    if (userIdx > 0 && userIdx < args.Length && passIdx > 0 && passIdx < args.Length)
    {
        try
        {
            var authService = application.Services.GetRequiredService<IAdminAuthService>();
            await authService.ResetPasswordAsync(args[userIdx], args[passIdx]);
            Console.WriteLine($"Admin user '{args[userIdx]}' credentials updated successfully.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to set admin user: {ex.Message}");
        }
        return;
    }
}

var options = application.Services.GetRequiredService<IOptions<ConnectionOptions>>();

// Controllers maps
application.MapDefaultControllerRoute();

// Start the application
application.Run();

// Required for tests
public partial class Program { }