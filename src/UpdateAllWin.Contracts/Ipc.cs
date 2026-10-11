using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateAllWin.Contracts;

/// <summary>
/// The complete, closed set of operations the service accepts. There is deliberately no
/// "run command"/"run file" style request: clients can only ask for named operations.
/// </summary>
public enum IpcCommand
{
    Ping,
    GetStatus,
    GetDevice,
    StartScan,
    /// <summary>Scan, then install everything eligible under policy (BIOS still needs approval).</summary>
    StartUpdateAll,
    /// <summary>Payload: <see cref="InstallRequest"/>.</summary>
    InstallSelected,
    /// <summary>Payload: list of update keys. Returns <see cref="BiosSafetyReport"/>.</summary>
    GetBiosSafetyReport,
    Cancel,
    /// <summary>Payload: <see cref="HistoryQuery"/>.</summary>
    GetHistory,
    GetPolicy,
    /// <summary>Administrators only. Payload: <see cref="MachinePolicy"/>.</summary>
    SetPolicy,
    /// <summary>Administrators only. Payload: update key.</summary>
    ExcludeUpdate,
    /// <summary>Administrators only. Payload: update key.</summary>
    IncludeUpdate,
    /// <summary>Returns the path of a zip in %ProgramData%\UpdateAllWin\Exports.</summary>
    ExportDiagnostics,
    /// <summary>Restart the PC (only accepted when a restart is pending). Payload: delay seconds.</summary>
    RestartComputer,
    /// <summary>Record that the user chose "Restart later".</summary>
    DeferRestart,
}

public sealed record IpcRequest(IpcCommand Command, JsonElement? Payload = null);

public sealed record IpcResponse(bool Ok, string? Error = null, JsonElement? Payload = null, bool AccessDenied = false);

public sealed record InstallRequest(IReadOnlyList<string> Keys, IReadOnlyList<string> ApprovedBiosKeys);

public sealed record HistoryQuery(int Limit = 500, HistoryKind? Kind = null);

public static class IpcProtocol
{
    public const string PipeName = "UpdateAllWin.Service.v1";
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { } e && e.ValueKind != JsonValueKind.Null ? e.Deserialize<T>(Json) : default;

    /// <summary>Messages are a 4-byte little-endian length followed by UTF-8 JSON.</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("IPC message too large.");
        var header = BitConverter.GetBytes(bytes.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(header);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        if (!BitConverter.IsLittleEndian) Array.Reverse(header);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException($"Invalid IPC message length {length}.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, Json);
    }
}
