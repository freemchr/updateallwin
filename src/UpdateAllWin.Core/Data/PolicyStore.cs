using System.Text.Json;
using Microsoft.Extensions.Logging;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Core.Data;

public interface IPolicyStore
{
    MachinePolicy Current { get; }
    event EventHandler? Changed;
    void Save(MachinePolicy policy);
}

/// <summary>Machine policy in %ProgramData%\UpdateAllWin\policy.json (admin/SYSTEM writable only).</summary>
public sealed class JsonPolicyStore : IPolicyStore
{
    private readonly string _path;
    private readonly ILogger<JsonPolicyStore> _logger;
    private MachinePolicy _current;

    public JsonPolicyStore(string path, ILogger<JsonPolicyStore> logger)
    {
        _path = path;
        _logger = logger;
        _current = Load();
    }

    public MachinePolicy Current => _current;

    public event EventHandler? Changed;

    public void Save(MachinePolicy policy)
    {
        var validated = Validate(policy);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(validated, PolicyJson));
        File.Move(temp, _path, overwrite: true);
        _current = validated;
        WriteUninstallPreference(validated.RemoveDataOnUninstall);
        _logger.LogInformation("Policy saved");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Mirrors "delete data on uninstall" to HKLM\SOFTWARE\UpdateAllWin so the MSI can read it
    /// (the installer can't parse policy.json).
    /// </summary>
    private void WriteUninstallPreference(bool removeData)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\UpdateAllWin");
            key.SetValue("RemoveDataOnUninstall", removeData ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug("Could not record uninstall preference: {Message}", ex.Message);
        }
    }

    private MachinePolicy Load()
    {
        try
        {
            if (File.Exists(_path))
                return Validate(JsonSerializer.Deserialize<MachinePolicy>(File.ReadAllText(_path), PolicyJson) ?? new());
        }
        catch (Exception ex)
        {
            _logger.LogError("Policy file is invalid, using defaults: {Message}", ex.Message);
        }
        return new MachinePolicy();
    }

    /// <summary>Clamps values so a malformed policy can't produce unsafe behaviour.</summary>
    public static MachinePolicy Validate(MachinePolicy p) => p with
    {
        ScanHour = Math.Clamp(p.ScanHour, 0, 23),
        QuietHoursStart = Math.Clamp(p.QuietHoursStart, 0, 23),
        QuietHoursEnd = Math.Clamp(p.QuietHoursEnd, 0, 23),
        BiosMinimumBatteryPercent = Math.Clamp(p.BiosMinimumBatteryPercent, 20, 100),
        MinimumFreeSpaceGb = Math.Clamp(p.MinimumFreeSpaceGb, 1, 500),
        RestartReminderMinutes = Math.Clamp(p.RestartReminderMinutes, 15, 24 * 60),
        MaxRestartDeferralHours = Math.Clamp(p.MaxRestartDeferralHours, 0, 24 * 30),
        LogRetentionDays = Math.Clamp(p.LogRetentionDays, 1, 3650),
        ExcludedUpdates = (p.ExcludedUpdates ?? []).Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList(),
        HpiaPath = string.IsNullOrWhiteSpace(p.HpiaPath) ? null : p.HpiaPath.Trim(),
        HpBiosPasswordFile = string.IsNullOrWhiteSpace(p.HpBiosPasswordFile) ? null : p.HpBiosPasswordFile.Trim(),
        DownloadFolder = string.IsNullOrWhiteSpace(p.DownloadFolder) ? null : p.DownloadFolder.Trim(),
    };

    private static readonly JsonSerializerOptions PolicyJson = new(IpcProtocol.Json) { WriteIndented = true };
}
