using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cs2ServerControl.Services;

/// <summary>
/// Tiny read-only web server for CS 1.6 fast downloads (sv_downloadurl).
/// Without it, clients pull maps through the game connection at a crawl. It
/// only serves files with map related extensions from a few folders of
/// cstrike, so configs, passwords and plugins stay private. A raw TCP
/// listener on purpose: HttpListener would need administrator rights or a
/// URL reservation for a port on all addresses.
/// </summary>
public sealed class FastDownloadServer {
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".bsp", ".wad", ".res", ".txt", ".wav", ".mp3", ".mdl", ".spr", ".tga", ".bmp", ".nav", ".bz2",
    };

    private static readonly HashSet<string> AllowedFolders = new(StringComparer.OrdinalIgnoreCase) {
        "maps", "sound", "models", "sprites", "gfx", "overviews",
    };

    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private const int CopyBufferBytes = 64 * 1024;

    private readonly string _rootFullPath;
    private readonly int _port;

    public FastDownloadServer(string root, int port) {
        _rootFullPath = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        _port = port;
    }

    /// <summary>Serves until cancelled. Clients are handled one after another, like the game asks.</summary>
    public async Task RunAsync(CancellationToken cancellation) {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        try {
            while (!cancellation.IsCancellationRequested) {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
                client.ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds;
                client.SendTimeout = (int)SendTimeout.TotalMilliseconds;
                try {
                    await HandleAsync(client, cancellation);
                } catch (Exception exception) when (exception is IOException or SocketException) {
                    // A client that hangs up mid-download only ends its own request.
                }
            }
        } catch (OperationCanceledException) {
            // Normal shutdown.
        } finally {
            listener.Stop();
        }
    }

    /// <summary>
    /// Maps a request path to a file under the root, or null when the request
    /// must not be served.
    /// </summary>
    internal string? ResolveRequestedFile(string requestPath) {
        string decodedPath = Uri.UnescapeDataString(requestPath.Split('?')[0]).TrimStart('/');
        if (string.IsNullOrWhiteSpace(decodedPath)) {
            return null;
        }

        string fullPath;
        try {
            fullPath = Path.GetFullPath(Path.Combine(_rootFullPath, decodedPath.Replace('/', '\\')));
        } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            return null;
        }

        // GetFullPath resolves "..", so this check stops any escape from the root.
        if (!fullPath.StartsWith(_rootFullPath, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        // Only the folders a client may need, plus map textures (.wad) in the root.
        string relativePath = fullPath[_rootFullPath.Length..];
        string topFolder = relativePath.Split('\\')[0];
        bool isRootWad = !relativePath.Contains('\\') && relativePath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase);
        if (!AllowedFolders.Contains(topFolder) && !isRootWad) {
            return null;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)) || !File.Exists(fullPath)) {
            return null;
        }
        return fullPath;
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellation) {
        NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        // Only the request line matters, e.g. "GET /maps/de_rats.bsp HTTP/1.1".
        // The headers are read up to the empty line so the client is not cut off.
        string? requestLine = await reader.ReadLineAsync(cancellation);
        while (true) {
            string? headerLine = await reader.ReadLineAsync(cancellation);
            if (string.IsNullOrEmpty(headerLine)) {
                break;
            }
        }
        if (requestLine == null) {
            return;
        }

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2) {
            return;
        }

        string method = parts[0].ToUpperInvariant();
        string? filePath = null;
        if (method is "GET" or "HEAD") {
            filePath = ResolveRequestedFile(parts[1]);
        }
        await SendResponseAsync(stream, filePath, method == "HEAD", cancellation);
    }

    private static async Task SendResponseAsync(NetworkStream stream, string? filePath, bool isHeadRequest, CancellationToken cancellation) {
        if (filePath == null) {
            await WriteHeaderAsync(stream, "HTTP/1.1 404 Not Found", 0, cancellation);
            return;
        }

        await using var file = File.OpenRead(filePath);
        await WriteHeaderAsync(stream, "HTTP/1.1 200 OK", file.Length, cancellation);
        if (!isHeadRequest) {
            await file.CopyToAsync(stream, CopyBufferBytes, cancellation);
        }
    }

    private static async Task WriteHeaderAsync(NetworkStream stream, string statusLine, long contentLength, CancellationToken cancellation) {
        string header = $"{statusLine}\r\nContent-Type: application/octet-stream\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellation);
    }
}
