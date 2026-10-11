using Microsoft.Extensions.DependencyInjection;
using UpdateAllWin.Core.Adapters;
using UpdateAllWin.ManufacturerAdapters.Dell;
using UpdateAllWin.ManufacturerAdapters.HP;
using UpdateAllWin.ManufacturerAdapters.Lenovo;
using UpdateAllWin.ManufacturerAdapters.Simulation;
using UpdateAllWin.ManufacturerAdapters.WindowsUpdate;

namespace UpdateAllWin.ManufacturerAdapters;

public static class AdapterServiceCollectionExtensions
{
    /// <summary>
    /// Registers every manufacturer adapter. To add an OEM (ASUS, Acer, MSI, Surface...), implement
    /// <see cref="IManufacturerAdapter"/> and register it here; the engine needs no changes.
    /// </summary>
    public static IServiceCollection AddManufacturerAdapters(this IServiceCollection services)
    {
        services.AddSingleton<IManufacturerAdapter, HpImageAssistantAdapter>();
        services.AddSingleton<IManufacturerAdapter, DellCommandUpdateAdapter>();
        services.AddSingleton<IManufacturerAdapter, LenovoAdapter>();
        services.AddSingleton<IManufacturerAdapter, WindowsUpdateAdapter>();
        services.AddSingleton<IManufacturerAdapter, SimulatedAdapter>(_ => new SimulatedAdapter());
        return services;
    }
}
