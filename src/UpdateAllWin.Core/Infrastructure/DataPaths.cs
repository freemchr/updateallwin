using System.Security.AccessControl;
using System.Security.Principal;

namespace UpdateAllWin.Core.Infrastructure;

/// <summary>Locations of service data. Defaults to %ProgramData%\UpdateAllWin; tests use a temp root.</summary>
public sealed class DataPaths
{
    public DataPaths(string? root = null, string? downloadFolder = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpdateAllWin");
        Downloads = string.IsNullOrWhiteSpace(downloadFolder) ? Path.Combine(Root, "Downloads") : downloadFolder;
    }

    public string Root { get; }
    public string Logs => Ensure(Path.Combine(Root, "Logs"));
    public string Tools => Ensure(Path.Combine(Root, "Tools"));
    public string Work => Ensure(Path.Combine(Root, "Work"));
    public string Exports => Ensure(Path.Combine(Root, "Exports"));
    public string Downloads { get; }
    public string Database => Path.Combine(Ensure(Root), "updateall.db");
    public string PolicyFile => Path.Combine(Ensure(Root), "policy.json");

    public DataPaths WithDownloadFolder(string? folder) => new(Root, folder);

    public string NewWorkFolder(string name)
    {
        var path = Path.Combine(Work, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(path);
        return path;
    }

    public string NewDownloadFolder(string name)
    {
        var path = Path.Combine(Ensure(Downloads), $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Locks the data folder down to SYSTEM and Administrators (full) and Users (read).
    /// By default users can create files under %ProgramData%, which would let a standard
    /// user plant a "vendor tool" that the SYSTEM service later runs.
    /// </summary>
    public void Secure(string? folder = null)
    {
        var target = Ensure(folder ?? Root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(target).SetAccessControl(security);
    }

    public void CleanupOlderThan(TimeSpan age)
    {
        foreach (var root in new[] { Work, Downloads, Exports })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(dir) > age) Directory.Delete(dir, recursive: true);
                }
                catch { /* in use; try next time */ }
            }
            foreach (var file in Directory.EnumerateFiles(root))
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(file) > age) File.Delete(file);
                }
                catch { }
            }
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
