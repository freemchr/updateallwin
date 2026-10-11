using UpdateAllWin.Contracts;

namespace UpdateAllWin.Core.Scheduling;

/// <summary>Pure schedule maths (local time), kept separate so it can be unit tested.</summary>
public static class Schedule
{
    /// <summary>
    /// Next scheduled scan after <paramref name="lastScanLocal"/>. If a run was missed (PC off),
    /// the result is in the past and the scan should run as soon as possible (outside quiet hours).
    /// </summary>
    public static DateTime? NextScan(MachinePolicy policy, DateTime? lastScanLocal, DateTime nowLocal)
    {
        if (policy.ScanFrequency == ScanFrequency.Manual) return null;
        if (lastScanLocal is null) return nowLocal; // never scanned: scan now

        var last = lastScanLocal.Value;
        var candidate = last.Date.AddHours(policy.ScanHour);
        if (policy.ScanFrequency == ScanFrequency.Daily)
        {
            if (candidate <= last) candidate = candidate.AddDays(1);
            return candidate;
        }

        // Weekly: next configured weekday at ScanHour strictly after the last scan.
        var days = ((int)policy.ScanDay - (int)candidate.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(days);
        if (candidate <= last) candidate = candidate.AddDays(7);
        return candidate;
    }

    public static bool IsInQuietHours(MachinePolicy policy, DateTime nowLocal)
    {
        if (!policy.QuietHoursEnabled || policy.QuietHoursStart == policy.QuietHoursEnd) return false;
        var h = nowLocal.Hour;
        return policy.QuietHoursStart < policy.QuietHoursEnd
            ? h >= policy.QuietHoursStart && h < policy.QuietHoursEnd
            : h >= policy.QuietHoursStart || h < policy.QuietHoursEnd; // wraps midnight, e.g. 22-7
    }
}
