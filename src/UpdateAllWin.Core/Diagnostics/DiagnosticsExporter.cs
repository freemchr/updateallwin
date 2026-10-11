using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UpdateAllWin.Contracts;
using UpdateAllWin.Core.Data;
using UpdateAllWin.Core.Device;
using UpdateAllWin.Core.Infrastructure;

namespace UpdateAllWin.Core.Diagnostics;

/// <summary>Builds a support zip: logs, vendor reports, policy, history and device info (serial masked).</summary>
public sealed class DiagnosticsExporter(DataPaths paths, IHistoryStore history, IPolicyStore policy, IDeviceInfoProvider device)
{
    public string Export()
    {
        var target = Path.Combine(paths.Exports, $"UpdateAllWin-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);

        AddFolder(zip, paths.Logs, "Logs", TimeSpan.FromDays(14));
        AddFolder(zip, paths.Work, "Reports", TimeSpan.FromDays(7));

        var info = device.GetDeviceInfo();
        AddText(zip, "device.json", JsonSerializer.Serialize(info with { SerialNumber = Mask(info.SerialNumber) }, Indented));
        AddText(zip, "policy.json", JsonSerializer.Serialize(policy.Current, Indented));
        AddText(zip, "history.csv", HistoryCsv.Build(history.Query(2000)));
        return target;
    }

    public static string Mask(string serial) =>
        serial.Length <= 4 ? new string('•', serial.Length) : new string('•', serial.Length - 4) + serial[^4..];

    private static void AddFolder(ZipArchive zip, string folder, string prefix, TimeSpan maxAge)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (DateTime.Now - File.GetLastWriteTime(file) > maxAge) continue;
            var name = Path.Combine(prefix, Path.GetRelativePath(folder, file)).Replace('\\', '/');
            try
            {
                // Open with sharing so files the logger is writing can still be copied.
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                source.CopyTo(entry);
            }
            catch (IOException) { }
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static readonly JsonSerializerOptions Indented = new(IpcProtocol.Json) { WriteIndented = true };
}

public static class HistoryCsv
{
    public static string Build(IEnumerable<HistoryEntry> entries)
    {
        var sb = new StringBuilder("Time,Kind,Trigger,Title,Result,Success,Source,Category,From,To,ExitCode,Details\r\n");
        foreach (var e in entries)
        {
            sb.AppendJoin(',',
                Csv(e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")), Csv(e.Kind.ToString()), Csv(e.Trigger?.ToString()),
                Csv(e.Title), Csv(e.Result), Csv(e.Success ? "Yes" : "No"), Csv(e.AdapterId), Csv(e.Category?.ToString()),
                Csv(e.FromVersion), Csv(e.ToVersion), Csv(e.ExitCode?.ToString()), Csv(e.Details));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        // Neutralise spreadsheet formula injection and quote everything.
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
