using System.Text.Json;
using System.Xml.Linq;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.ManufacturerAdapters.HP;

public sealed record HpiaRecommendation(
    string SoftPaqId,
    string Name,
    UpdateCategory Category,
    string CurrentVersion,
    string AvailableVersion,
    string? Comments,
    string? ReleaseNotesUrl);

public sealed record HpiaRemediation(string SoftPaqId, string Status, int? ReturnCode, string? Description);

/// <summary>
/// Parses HPIA analysis reports. HP does not publish a report schema, so this follows the
/// structure observed in community tooling: XML is primary
/// (HPIA/Recommendations/{BIOS|Drivers|Software|Firmware}/Recommendation), JSON is used for
/// per-SoftPaq install results (HPIA.Recommendations[].Remediation). File names vary, so we glob.
/// </summary>
public static class HpiaReportParser
{
    public static IReadOnlyList<HpiaRecommendation> ParseFolder(string folder)
    {
        var xml = Newest(folder, "*.xml");
        if (xml is not null)
        {
            var fromXml = ParseXml(XDocument.Load(xml));
            if (fromXml.Count > 0) return fromXml;
        }
        var json = Newest(folder, "*.json");
        return json is null ? [] : ParseJson(File.ReadAllText(json));
    }

    public static IReadOnlyList<HpiaRecommendation> ParseXml(XDocument doc)
    {
        var list = new List<HpiaRecommendation>();
        foreach (var rec in doc.Descendants().Where(e => Is(e, "Recommendation")))
        {
            var group = rec.Parent;
            if (group is null || group.Parent is null || !Is(group.Parent, "Recommendations")) continue;

            var softpaq = rec.Descendants().FirstOrDefault(e => Is(e, "Softpaq") || Is(e, "SoftPaq"));
            var id = Value(softpaq, "Id") ?? Value(rec, "SoftPaqId") ?? "";
            if (id.Length == 0) continue;
            var component = Value(rec, "TargetComponent") ?? "";
            list.Add(new HpiaRecommendation(
                NormalizeId(id),
                Value(softpaq, "Name") ?? component,
                CategoryFrom(group.Name.LocalName, component),
                Value(rec, "TargetVersion") ?? "",
                Value(rec, "ReferenceVersion") ?? Value(softpaq, "Version") ?? "",
                Value(rec, "Comments"),
                Url(Value(softpaq, "ReleaseNotesUrl"))));
        }
        return list;
    }

    public static IReadOnlyList<HpiaRecommendation> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<HpiaRecommendation>();
        foreach (var rec in Recommendations(doc.RootElement))
        {
            var id = Str(rec, "SoftPaqId", "SoftPaqID", "Id");
            if (string.IsNullOrEmpty(id)) continue;
            var component = Str(rec, "TargetComponent") ?? "";
            list.Add(new HpiaRecommendation(
                NormalizeId(id),
                Str(rec, "Name") ?? component,
                CategoryFrom(Str(rec, "Category", "Type") ?? "", component),
                Str(rec, "TargetVersion") ?? "",
                Str(rec, "ReferenceVersion") ?? "",
                Str(rec, "Comments"),
                Url(Str(rec, "ReleaseNotesUrl"))));
        }
        return list;
    }

    /// <summary>Per-SoftPaq install results from the JSON report written by /Action:Install.</summary>
    public static IReadOnlyDictionary<string, HpiaRemediation> ParseRemediation(string folder)
    {
        var result = new Dictionary<string, HpiaRemediation>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var rec in Recommendations(doc.RootElement))
                {
                    var id = Str(rec, "SoftPaqId", "SoftPaqID", "Id");
                    if (id is null || !rec.TryGetProperty("Remediation", out var rem) || rem.ValueKind != JsonValueKind.Object) continue;
                    int? code = rem.TryGetProperty("ReturnCode", out var rc) && rc.TryGetInt32(out var c) ? c : null;
                    result[NormalizeId(id)] = new HpiaRemediation(NormalizeId(id), Str(rem, "Status") ?? "", code, Str(rem, "ReturnDescription"));
                }
            }
            catch (JsonException) { }
        }
        return result;
    }

    /// <summary>"sp151063" / "SP151063.exe" → "sp151063". /SPList wants the bare number.</summary>
    public static string NormalizeId(string id)
    {
        var s = id.Trim().ToLowerInvariant();
        if (s.EndsWith(".exe")) s = s[..^4];
        return s.StartsWith("sp") ? s : "sp" + s;
    }

    public static string SpListNumber(string id) => NormalizeId(id)[2..];

    private static UpdateCategory CategoryFrom(string group, string component)
    {
        var g = (group + " " + component).ToLowerInvariant();
        if (g.Contains("bios")) return UpdateCategory.Bios;
        if (g.Contains("firmware")) return UpdateCategory.Firmware;
        if (g.Contains("software")) return UpdateCategory.Software;
        return UpdateCategory.Driver;
    }

    private static IEnumerable<JsonElement> Recommendations(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (p.NameEquals("Recommendations") && p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var r in p.Value.EnumerateArray()) yield return r;
                else
                    foreach (var r in Recommendations(p.Value)) yield return r;
            }
        }
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return p.Value.ToString().Trim();
        return null;
    }

    private static bool Is(XElement e, string name) => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? Value(XElement? e, string child)
    {
        var v = e?.Elements().FirstOrDefault(c => Is(c, child))?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string? Url(string? u) =>
        string.IsNullOrWhiteSpace(u) ? null : u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : "https://" + u.TrimStart('/');

    private static string? Newest(string folder, string pattern) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
}
