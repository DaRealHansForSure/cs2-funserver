using System.Net;
using System.Net.Sockets;

namespace Cs2ServerControl.Services;

/// <summary>
/// Receives the fun_livemap.amxx packets on 127.0.0.1:27090. Only loopback:
/// the feed shows every position, it must never reach the LAN.
/// </summary>
public sealed class LiveFeedListener : IDisposable {
    public const int Port = 27090;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private readonly LiveFeedParser _parser = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DateTime> _recentSnapshots = new();
    private readonly object _rateLock = new();
    private DateTime _lastSnapshotAt = DateTime.MinValue;

    /// <summary>Raised on a thread pool thread.</summary>
    public event EventHandler<LiveSnapshot>? SnapshotReceived;

    /// <summary>Raised on a thread pool thread.</summary>
    public event EventHandler<LiveKill>? KillReceived;

    /// <summary>Raised on a thread pool thread.</summary>
    public event EventHandler<LiveShotBatch>? ShotsReceived;

    public bool HasLiveData => DateTime.UtcNow - _lastSnapshotAt < StaleAfter;

    public int MalformedLineCount => _parser.MalformedLineCount;

    /// <summary>Snapshot packets in the last second.</summary>
    public int PacketsPerSecond {
        get {
            lock (_rateLock) {
                DropOldTimestamps();
                return _recentSnapshots.Count;
            }
        }
    }

    public string? StartError { get; private set; }

    public void Start() {
        UdpClient client;
        try {
            client = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        } catch (SocketException exception) {
            StartError = $"Port {Port} is in use ({exception.SocketErrorCode}).";
            return;
        }
        _ = ReceiveLoopAsync(client);
    }

    public void Dispose() {
        _stop.Cancel();
    }

    private async Task ReceiveLoopAsync(UdpClient client) {
        using (client) {
            while (!_stop.IsCancellationRequested) {
                UdpReceiveResult result;
                try {
                    result = await client.ReceiveAsync(_stop.Token);
                } catch (OperationCanceledException) {
                    return;
                } catch (SocketException) {
                    // Windows reports ICMP "port unreachable" of earlier sends as a
                    // receive error on UDP sockets; the next packet is fine.
                    continue;
                }

                object? parsed = _parser.Parse(result.Buffer);
                if (parsed is LiveSnapshot snapshot) {
                    RecordSnapshot();
                    SnapshotReceived?.Invoke(this, snapshot);
                } else if (parsed is LiveKill kill) {
                    KillReceived?.Invoke(this, kill);
                } else if (parsed is LiveShotBatch shots) {
                    ShotsReceived?.Invoke(this, shots);
                }
            }
        }
    }

    private void RecordSnapshot() {
        lock (_rateLock) {
            _lastSnapshotAt = DateTime.UtcNow;
            _recentSnapshots.Enqueue(_lastSnapshotAt);
            DropOldTimestamps();
        }
    }

    private void DropOldTimestamps() {
        DateTime oneSecondAgo = DateTime.UtcNow.AddSeconds(-1);
        while (_recentSnapshots.Count > 0 && _recentSnapshots.Peek() < oneSecondAgo) {
            _recentSnapshots.Dequeue();
        }
    }
}
