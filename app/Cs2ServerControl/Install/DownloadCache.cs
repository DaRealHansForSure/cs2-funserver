using System.IO;
using System.Net.Http;

namespace Cs2ServerControl.Install;

/// <summary>
/// Downloads into tools\downloads. A cached file is reused, so running the
/// installer again is safe and only fetches what is missing.
/// </summary>
internal sealed class DownloadCache {
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly string _downloadDir;
    private readonly InstallLog _log;

    public DownloadCache(string downloadDir, InstallLog log) {
        _downloadDir = downloadDir;
        _log = log;
    }

    /// <summary>One client for every request; GameBanana answers browsers only.</summary>
    public static HttpClient Client => SharedClient;

    /// <summary>Downloads a file into the cache and returns its path. Cached files are reused.</summary>
    public async Task<string> GetAsync(string url, string fileName, CancellationToken cancellation) {
        string targetPath = Path.Combine(_downloadDir, fileName);
        if (File.Exists(targetPath)) {
            return targetPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        _log.Note($"Downloading {fileName} ...");
        await DownloadFileAsync(url, targetPath, cancellation);
        return targetPath;
    }

    /// <summary>
    /// Downloads to a temporary name first, so an aborted download never
    /// looks like a finished one on the next run.
    /// </summary>
    public static async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellation) {
        string partialPath = targetPath + ".part";
        using (HttpResponseMessage response = await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)) {
            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
            await using FileStream file = File.Create(partialPath);
            await body.CopyToAsync(file, cancellation);
        }
        File.Move(partialPath, targetPath, overwrite: true);
    }

    private static HttpClient CreateClient() {
        var handler = new SocketsHttpHandler {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        // Map archives are a few MB, but a LAN party line can be slow.
        var client = new HttpClient(handler) {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(InstallComponents.BrowserUserAgent);
        return client;
    }
}
