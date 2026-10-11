using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Diagnostics;
using UpdateAllWin.Core.Engine;
using UpdateAllWin.Core.Infrastructure;
using UpdateAllWin.Core.Safety;

namespace UpdateAllWin.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the engine and its Windows implementations. Adapters are registered separately.</summary>
    public static IServiceCollection AddUpdateAllCore(this IServiceCollection services, DataPaths? paths = null)
    {
        paths ??= new DataPaths();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new EngineOptions());
        services.AddSingleton<SqliteStore>(_ => new SqliteStore(paths.Database));
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<IPolicyStore>(sp => new JsonPolicyStore(paths.PolicyFile, sp.GetRequiredService<ILogger<JsonPolicyStore>>()));
        services.AddSingleton<IDeviceInfoProvider, DeviceInfoProvider>();
        services.AddSingleton<ISystemSafety, WindowsSystemSafety>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IDownloader, HttpDownloader>();
        services.AddSingleton<DiagnosticsExporter>();
        services.AddSingleton<UpdateEngine>();
        return services;
    }
}
