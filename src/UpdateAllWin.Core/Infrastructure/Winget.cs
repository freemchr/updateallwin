using Microsoft.Extensions.Logging;

namespace UpdateAllWin.Core.Infrastructure;

/// <summary>Installs vendor tools with the Windows Package Manager when they are missing.</summary>
public static class Winget
{
    // APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE / ..._PACKAGE_ALREADY_INSTALLED
    private static readonly int[] AlreadyInstalled = [-1978335189, -1978335135];

    public static async Task<bool> InstallAsync(IProcessRunner processes, ILogger logger, string packageId, CancellationToken ct)
    {
        var exe = Locate();
        if (exe is null)
        {
            logger.LogWarning("winget was not found; cannot install {Package} automatically", packageId);
            return false;
        }

        logger.LogInformation("Installing {Package} with winget", packageId);
        var result = await processes.RunAsync(exe,
            $"install --id {packageId} --exact --silent --scope machine --source winget " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            ct, timeout: TimeSpan.FromMinutes(30));
        return result.ExitCode == 0 || AlreadyInstalled.Contains(result.ExitCode);
    }

    /// <summary>
    /// Finds winget.exe. Under LocalSystem there is no per-user App Execution Alias, so look in
    /// the App Installer package folder directly.
    /// </summary>
    private static string? Locate()
    {
        try
        {
            var appsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var fromPackage = Directory.EnumerateDirectories(appsRoot, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "winget.exe"))
                .FirstOrDefault(File.Exists);
            if (fromPackage is not null) return fromPackage;
        }
        catch
        {
            // WindowsApps is not listable for normal admins; fall through to the alias.
        }

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }
}
