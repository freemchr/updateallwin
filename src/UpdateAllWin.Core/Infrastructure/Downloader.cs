using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace UpdateAllWin.Core.Infrastructure;

public interface IDownloader
{
    Task<string> GetStringAsync(string url, CancellationToken ct);

    /// <summary>Downloads to <paramref name="destination"/>; verifies SHA-256 when given.</summary>
    Task DownloadFileAsync(string url, string destination, CancellationToken ct, string? expectedSha256 = null, IProgress<int>? percent = null);
}

public sealed class HttpDownloader(ILogger<HttpDownloader> logger) : IDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UpdateAllWin/{typeof(HttpDownloader).Assembly.GetName().Version}");
        return client;
    }

    public Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        RequireHttps(url);
        return Http.GetStringAsync(url, ct);
    }

    public async Task DownloadFileAsync(string url, string destination, CancellationToken ct, string? expectedSha256 = null, IProgress<int>? percent = null)
    {
        RequireHttps(url);
        logger.LogInformation("Downloading {Url}", url);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        var temp = destination + ".partial";
        using (var sha = SHA256.Create())
        {
            await using (var file = File.Create(temp))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n, lastPercent = -1;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    read += n;
                    if (total > 0 && percent is not null)
                    {
                        var p = (int)(read * 100 / total.Value);
                        if (p != lastPercent) percent.Report(lastPercent = p);
                    }
                }
            }
            sha.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexString(sha.Hash!);
            if (expectedSha256 is not null && !actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new InvalidDataException($"Checksum mismatch for {Path.GetFileName(destination)}.");
            }
        }
        File.Move(temp, destination, overwrite: true);
        logger.LogInformation("Downloaded {File} ({Size:N0} bytes)", Path.GetFileName(destination), new FileInfo(destination).Length);
    }

    private static void RequireHttps(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing non-HTTPS download: {url}");
    }
}
