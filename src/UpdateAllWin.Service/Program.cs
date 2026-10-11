using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Core;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.ManufacturerAdapters;
using UpdateAllWin.Service;

// UpdateAllService.exe            -> runs the Windows service (or in a console for development)
// UpdateAllService.exe <command>  -> command line for scripts/RMM, see `UpdateAllService.exe help`
if (args.Length > 0 && CommandLine.IsCommand(args[0]))
    return await CommandLine.RunAsync(args);

// Pin the content root to the install folder: started from System32 (as services and some launchers are),
// the default would watch System32 for configuration changes.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = ServiceHost.ServiceName);
ServiceHost.ConfigureServices(builder.Services, builder.Logging, "UpdateAllService");
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService<SchedulerService>();

var host = builder.Build();
await host.RunAsync();
return 0;

namespace UpdateAllWin.Service
{
    internal static class ServiceHost
    {
        public const string ServiceName = "UpdateAllService";

        /// <summary>Shared by the service host and the in-process command line.</summary>
        public static void ConfigureServices(IServiceCollection services, ILoggingBuilder logging, string logPrefix)
        {
            var paths = new DataPaths();
            if (IsPrivileged())
            {
                // Only lock the folder down when we're SYSTEM/elevated; a non-elevated developer
                // console would otherwise lock itself out of its own data folder.
                try { paths.Secure(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { }
            }

            logging.ClearProviders();
            logging.AddProvider(new FileLoggerProvider(paths.Logs, logPrefix, LogLevel.Debug));
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddFilter("System.Net.Http", LogLevel.Warning);
            if (Environment.UserInteractive) logging.AddSimpleConsole(o => o.SingleLine = true);

            services.AddUpdateAllCore(paths);
            services.AddManufacturerAdapters();
            if (!IsPrivileged())
            {
                // Global\ kernel objects need SeCreateGlobalPrivilege; use a per-session lock for dev runs.
                services.AddSingleton(new Core.Engine.EngineOptions { GlobalLockName = @"Local\UpdateAllWin.Engine" });
            }
        }

        public static bool IsPrivileged()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem || new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
