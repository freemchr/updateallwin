namespace UpdateAllWin.ManufacturerAdapters.Common;

public static class ToolResolver
{
    public static string ProgramFiles(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), .. parts]);

    public static string ProgramFilesX86(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), .. parts]);

    public static string? FirstExisting(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    /// <summary>
    /// Tools run as LocalSystem, so only accept tool paths an ordinary user can't write to
    /// (Program Files, Windows, or our secured %ProgramData% folder).
    /// </summary>
    public static bool IsTrustedLocation(string path, string dataRoot)
    {
        var full = Path.GetFullPath(path);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            dataRoot,
        ];
        return roots.Any(r => !string.IsNullOrEmpty(r) &&
                              full.StartsWith(Path.GetFullPath(r).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static string Quote(string path) => "\"" + path.TrimEnd('\\') + "\"";
}
