using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using UpdateAllWin.Core.Device;

namespace UpdateAllWin.Core.Safety;

public sealed record PowerSnapshot(bool OnAcPower, bool HasBattery, int? BatteryPercent);

public sealed record BitLockerState(bool ProtectionOn, bool HasRecoveryPassword, bool Known, string Detail);

/// <summary>OS-level checks and actions used around installs. Abstracted so the engine can be tested.</summary>
public interface ISystemSafety
{
    PowerSnapshot GetPower();
    BitLockerState GetBitLockerState();
    bool SuspendBitLockerForOneRestart(out string detail);
    long GetSystemDriveFreeBytes();
    bool IsRestartPendingFromOs();
    bool TryCreateRestorePoint(string description, out string detail);
    IDisposable PreventSleep(string reason);
    DateTime GetBootTimeUtc();
    void RestartComputer(int delaySeconds, string message);
}

public sealed class WindowsSystemSafety(ILogger<WindowsSystemSafety> logger) : ISystemSafety
{
    private const string BitLockerNamespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    public PowerSnapshot GetPower()
    {
        var percent = PowerInfo.BatteryPercent();
        return new PowerSnapshot(PowerInfo.IsOnAcPower(), percent is not null, percent);
    }

    public BitLockerState GetBitLockerState()
    {
        try
        {
            using var volume = GetSystemVolume();
            if (volume is null) return new BitLockerState(false, false, true, "BitLocker is not configured on the system drive");

            var status = volume.InvokeMethod("GetProtectionStatus", null, null);
            var protectionOn = Convert.ToUInt32(status["ProtectionStatus"]) == 1;

            var inParams = volume.GetMethodParameters("GetKeyProtectors");
            inParams["KeyProtectorType"] = 3u; // numerical password = 48-digit recovery password
            var protectors = volume.InvokeMethod("GetKeyProtectors", inParams, null);
            var hasRecovery = protectors["VolumeKeyProtectorID"] is string[] { Length: > 0 };

            return new BitLockerState(protectionOn, hasRecovery, true,
                !protectionOn ? "BitLocker protection is off"
                : hasRecovery ? "BitLocker is on and a recovery password protector exists"
                : "BitLocker is on but no recovery password protector was found");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read BitLocker state: {Message}", ex.Message);
            return new BitLockerState(false, false, false, $"BitLocker state unknown: {ex.Message}");
        }
    }

    public bool SuspendBitLockerForOneRestart(out string detail)
    {
        try
        {
            using var volume = GetSystemVolume();
            if (volume is null)
            {
                detail = "No BitLocker volume";
                return true;
            }
            var inParams = volume.GetMethodParameters("DisableKeyProtectors");
            inParams["DisableCount"] = 1u; // protectors re-enable automatically after one restart
            var result = volume.InvokeMethod("DisableKeyProtectors", inParams, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);
            detail = code == 0 ? "BitLocker suspended for one restart" : $"DisableKeyProtectors returned 0x{code:X8}";
            logger.LogInformation("{Detail}", detail);
            return code == 0;
        }
        catch (Exception ex)
        {
            detail = $"Could not suspend BitLocker: {ex.Message}";
            logger.LogError("{Detail}", detail);
            return false;
        }
    }

    public long GetSystemDriveFreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace; }
        catch { return long.MaxValue; }
    }

    public bool IsRestartPendingFromOs()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) return true;
            using (var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Pending restart check failed: {Message}", ex.Message);
        }
        return false;
    }

    public bool TryCreateRestorePoint(string description, out string detail)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var inParams = restore.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 10u; // DEVICE_DRIVER_INSTALL
            inParams["EventType"] = 100u;       // BEGIN_SYSTEM_CHANGE
            var result = restore.InvokeMethod("CreateRestorePoint", inParams, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);
            // Windows silently skips creation if another restore point was made in the last 24 hours.
            detail = code == 0 ? "Restore point created (or a recent one already exists)" : $"CreateRestorePoint returned {code} (System Protection may be off)";
            return code == 0;
        }
        catch (Exception ex)
        {
            detail = $"Restore point not created: {ex.Message}";
            return false;
        }
    }

    public IDisposable PreventSleep(string reason) => new PowerRequest(reason, logger);

    public DateTime GetBootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public void RestartComputer(int delaySeconds, string message)
    {
        logger.LogWarning("Restarting the computer in {Delay} seconds", delaySeconds);
        var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        Process.Start(new ProcessStartInfo(shutdown, $"/r /t {Math.Clamp(delaySeconds, 0, 600)} /d p:2:17 /c \"{message.Replace("\"", "'")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private static ManagementObject? GetSystemVolume()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        using var searcher = new ManagementObjectSearcher(BitLockerNamespace,
            $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'");
        foreach (var obj in searcher.Get()) return (ManagementObject)obj;
        return null;
    }

    /// <summary>Keeps the system awake (PowerSetRequest) for the lifetime of the object.</summary>
    private sealed class PowerRequest : IDisposable
    {
        private readonly IntPtr _handle;

        public PowerRequest(string reason, ILogger logger)
        {
            var context = new REASON_CONTEXT { Version = 0, Flags = 1 /* POWER_REQUEST_CONTEXT_SIMPLE_STRING */, SimpleReasonString = reason };
            _handle = PowerCreateRequest(ref context);
            if (_handle == IntPtr.Zero || _handle == new IntPtr(-1) ||
                !PowerSetRequest(_handle, 1 /* PowerRequestSystemRequired */))
            {
                logger.LogDebug("PowerSetRequest failed: {Error}", Marshal.GetLastWin32Error());
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero || _handle == new IntPtr(-1)) return;
            PowerClearRequest(_handle, 1);
            CloseHandle(_handle);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct REASON_CONTEXT
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerSetRequest(IntPtr handle, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerClearRequest(IntPtr handle, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
