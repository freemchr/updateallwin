using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UpdateAllWin.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    /// <summary>Runs a process hidden, capturing output. Kills the process tree on cancellation.</summary>
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct, string? workingDirectory = null, TimeSpan? timeout = null);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        logger.LogInformation("Running {File} {Arguments}", Path.GetFileName(fileName), arguments);

        var dir = workingDirectory ?? Path.GetDirectoryName(fileName);
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrEmpty(dir) ? Environment.SystemDirectory : dir,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        DataReceivedEventHandler onData = (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (output) output.AppendLine(e.Data);
            logger.LogDebug("  {Tool}: {Line}", Path.GetFileNameWithoutExtension(fileName), e.Data.Trim());
        };
        process.OutputDataReceived += onData;
        process.ErrorDataReceived += onData;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) limit.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (ct.IsCancellationRequested)
            {
                logger.LogWarning("{File} was cancelled", Path.GetFileName(fileName));
                throw;
            }
            logger.LogError("{File} timed out after {Timeout}", Path.GetFileName(fileName), timeout);
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout}.");
        }

        process.WaitForExit();
        logger.LogInformation("{File} exited with code {ExitCode}", Path.GetFileName(fileName), process.ExitCode);
        return new ProcessResult(process.ExitCode, output.ToString());
    }
}
