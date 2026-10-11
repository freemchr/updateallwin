using System.Runtime.InteropServices;

namespace UpdateAllWin.Core.Device;

/// <summary>AC/battery state via GetSystemPowerStatus.</summary>
public static class PowerInfo
{
    public static bool IsOnAcPower() =>
        !GetSystemPowerStatus(out var s) || s.ACLineStatus != 0; // 0 = offline, 1 = online, 255 = unknown

    public static int? BatteryPercent() =>
        GetSystemPowerStatus(out var s) && s.BatteryLifePercent <= 100 && (s.BatteryFlag & 128) == 0
            ? s.BatteryLifePercent
            : null; // 255 = unknown, flag 128 = no system battery

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
