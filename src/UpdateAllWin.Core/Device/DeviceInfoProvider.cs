using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Core.Device;

public enum Oem { Unknown, HP, Dell, Lenovo, Microsoft, Asus, Acer, Msi, Other }

public interface IDeviceInfoProvider
{
    DeviceInfo GetDeviceInfo(bool refresh = false);
}

public sealed class DeviceInfoProvider(ILogger<DeviceInfoProvider> logger) : IDeviceInfoProvider
{
    private DeviceInfo? _cached;

    public DeviceInfo GetDeviceInfo(bool refresh = false)
    {
        if (_cached is not null && !refresh)
            return _cached with { OnAcPower = PowerInfo.IsOnAcPower(), BatteryPercent = PowerInfo.BatteryPercent() };

        var manufacturer = Query("Win32_ComputerSystem", "Manufacturer");
        var model = Query("Win32_ComputerSystem", "Model");
        var productCode = Query("Win32_BaseBoard", "Product"); // HP platform ID / Dell board
        if (OemDetector.Classify(manufacturer) == Oem.Lenovo)
        {
            // Lenovo: Model holds the machine type model (e.g. 20XWCTO1WW); the friendly name is in Version.
            productCode = model;
            var friendly = Query("Win32_ComputerSystemProduct", "Version");
            if (!string.IsNullOrWhiteSpace(friendly)) model = friendly;
        }

        var biosDate = Query("Win32_BIOS", "ReleaseDate");
        var ram = long.TryParse(Query("Win32_ComputerSystem", "TotalPhysicalMemory"), out var r) ? r : 0;
        var hasBattery = !string.IsNullOrEmpty(Query("Win32_Battery", "DeviceID"));

        _cached = new DeviceInfo
        {
            Manufacturer = Normalize(manufacturer),
            Model = model,
            ProductCode = productCode,
            SerialNumber = Query("Win32_BIOS", "SerialNumber"),
            OsName = Query("Win32_OperatingSystem", "Caption").Replace("Microsoft ", ""),
            OsDisplayVersion = ReadOsDisplayVersion(),
            OsBuild = ReadOsBuild(),
            BiosVersion = Query("Win32_BIOS", "SMBIOSBIOSVersion"),
            BiosDate = biosDate.Length >= 8 && DateTime.TryParseExact(biosDate[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null,
            Cpu = Query("Win32_Processor", "Name").Replace("(R)", "®").Replace("(TM)", "™"),
            RamBytes = ram,
            SystemDiskBytes = SystemDriveSize(),
            Chassis = ReadChassis(hasBattery),
            HasBattery = hasBattery,
            BatteryPercent = PowerInfo.BatteryPercent(),
            OnAcPower = PowerInfo.IsOnAcPower(),
        };
        logger.LogInformation("Device: {Manufacturer} {Model} ({Product}), BIOS {Bios}, {Os} {Build}",
            _cached.Manufacturer, _cached.Model, _cached.ProductCode, _cached.BiosVersion, _cached.OsName, _cached.OsBuild);
        return _cached;
    }

    private static string Normalize(string manufacturer) =>
        OemDetector.Classify(manufacturer) switch
        {
            Oem.HP => "HP",
            Oem.Dell => "Dell",
            Oem.Lenovo => "Lenovo",
            _ => manufacturer,
        };

    private static string ReadOsDisplayVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("DisplayVersion") as string ?? "";
    }

    private static string ReadOsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        return key?.GetValue("UBR") is int ubr ? $"{build}.{ubr}" : build;
    }

    private static long SystemDriveSize()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).TotalSize; }
        catch { return 0; }
    }

    private ChassisKind ReadChassis(bool hasBattery)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["ChassisTypes"] is ushort[] { Length: > 0 } types)
                    {
                        // SMBIOS chassis types
                        return types[0] switch
                        {
                            8 or 9 or 10 or 14 or 31 or 32 => ChassisKind.Laptop,
                            30 => ChassisKind.Tablet,
                            13 => ChassisKind.AllInOne,
                            17 or 23 or 28 => ChassisKind.Server,
                            3 or 4 or 5 or 6 or 7 or 15 or 16 or 35 or 36 => ChassisKind.Desktop,
                            _ => hasBattery ? ChassisKind.Laptop : ChassisKind.Desktop,
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Chassis query failed: {Message}", ex.Message);
        }
        return hasBattery ? ChassisKind.Laptop : ChassisKind.Desktop;
    }

    private string Query(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
                using (obj)
                    return obj[property]?.ToString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            logger.LogWarning("WMI query {Class}.{Property} failed: {Message}", wmiClass, property, ex.Message);
        }
        return "";
    }
}

public static class OemDetector
{
    public static Oem Classify(string? manufacturer)
    {
        var m = (manufacturer ?? "").Trim().ToUpperInvariant();
        if (m.Length == 0) return Oem.Unknown;
        if (m is "HP" or "HPE" || m.StartsWith("HEWLETT")) return Oem.HP;
        if (m.StartsWith("DELL") || m.StartsWith("ALIENWARE")) return Oem.Dell;
        if (m.StartsWith("LENOVO")) return Oem.Lenovo;
        if (m.StartsWith("MICROSOFT")) return Oem.Microsoft;
        if (m.StartsWith("ASUS")) return Oem.Asus;
        if (m.StartsWith("ACER")) return Oem.Acer;
        if (m.StartsWith("MICRO-STAR") || m == "MSI") return Oem.Msi;
        return Oem.Other;
    }
}
