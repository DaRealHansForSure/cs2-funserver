using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cs2ServerControl.Services;

public sealed class RconException(string message) : Exception(message);

/// <summary>
/// Source RCON over TCP, which CS2 speaks on its game port: one login with
/// the password, then commands over the open connection. A long answer
/// comes in several packets, so every command is followed by an empty
/// "mirror" packet; the server answers in order, so its echo marks the end.
/// One call at a time, because the answers share the one connection.
/// </summary>
public sealed class RconClient {
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _nextRequestId = 1;

    public IPEndPoint? Server { get; set; }
    public string Password { get; set; } = "";

    /// <summary>True after the last call got an answer, false after it failed.</summary>
    public bool IsConnected { get; private set; }

    public event EventHandler? ConnectionChanged;

    public async Task<string> SendAsync(string command, CancellationToken cancellation = default) {
        if (Server == null) {
            throw new RconException("The server address is unknown.");
        }

        await _oneAtATime.WaitAsync(cancellation);
        try {
            NetworkStream stream = await ConnectAsync(cancellation);
            string answer = await ExecuteAsync(stream, command, cancellation);
            SetConnected(true);
            return answer.TrimEnd();
        } catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException && !cancellation.IsCancellationRequested) {
            Disconnect();
            SetConnected(false);
            throw new RconException("The server does not answer RCON requests. Is it running?");
        } catch (RconException) {
            Disconnect();
            SetConnected(false);
            throw;
        } finally {
            _oneAtATime.Release();
        }
    }

    /// <summary>Drops the connection, for example after the server restarted or the password changed.</summary>
    public void Reset() {
        Disconnect();
        SetConnected(false);
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken cancellation) {
        if (_stream != null && _client is { Connected: true }) {
            return _stream;
        }
        Disconnect();

        var client = new TcpClient(Server!.AddressFamily);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(Server, timeout.Token);
        }
        _client = client;
        _stream = client.GetStream();

        int loginId = NextId();
        // A zero byte would end the password early; the protocol has no escaping.
        await SourceRconPacket.WriteAsync(_stream, loginId, SourceRconPacket.Auth, Password.Replace("\0", ""), cancellation);
        while (true) {
            SourceRconPacket reply = await ReadWithTimeoutAsync(_stream, cancellation);
            if (reply.Type != SourceRconPacket.AuthResponse) {
                // The server sends an empty value packet before the login answer.
                continue;
            }
            if (reply.Id == SourceRconPacket.LoginFailedId) {
                throw new RconException("The server refused the RCON password. Restart the server from this window.");
            }
            return _stream;
        }
    }

    private async Task<string> ExecuteAsync(NetworkStream stream, string command, CancellationToken cancellation) {
        int commandId = NextId();
        int mirrorId = NextId();
        await SourceRconPacket.WriteAsync(stream, commandId, SourceRconPacket.ExecCommand, command, cancellation);
        await SourceRconPacket.WriteAsync(stream, mirrorId, SourceRconPacket.ResponseValue, "", cancellation);

        var answer = new StringBuilder();
        while (true) {
            SourceRconPacket reply = await ReadWithTimeoutAsync(stream, cancellation);
            if (reply.Id == mirrorId) {
                return answer.ToString();
            }
            if (reply.Id == commandId) {
                answer.Append(reply.Body);
            }
        }
    }

    private static async Task<SourceRconPacket> ReadWithTimeoutAsync(NetworkStream stream, CancellationToken cancellation) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReplyTimeout);
        return await SourceRconPacket.ReadAsync(stream, timeout.Token);
    }

    private int NextId() {
        // Positive and never -1, which the server uses for a failed login.
        int id = _nextRequestId;
        _nextRequestId = _nextRequestId >= int.MaxValue - 1 ? 1 : _nextRequestId + 1;
        return id;
    }

    private void Disconnect() {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    private void SetConnected(bool isConnected) {
        if (IsConnected == isConnected) {
            return;
        }
        IsConnected = isConnected;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// One Source RCON packet: little endian size, id and type, then the body
/// and two zero bytes. Separate from the client so the tests reach it
/// without a socket.
/// </summary>
internal readonly record struct SourceRconPacket(int Id, int Type, string Body) {
    public const int ResponseValue = 0;
    public const int ExecCommand = 2;
    public const int AuthResponse = 2;
    public const int Auth = 3;
    public const int LoginFailedId = -1;

    // The server rejects bigger packets; long commands are not something the window sends.
    private const int MaxPacketSize = 4096;

    public static byte[] Encode(int id, int type, string body) {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        int size = 4 + 4 + bodyBytes.Length + 2;
        byte[] packet = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        bodyBytes.CopyTo(packet, 12);
        return packet;
    }

    /// <summary>Reads one packet from its size and payload; null when the bytes are no packet.</summary>
    public static SourceRconPacket? Decode(ReadOnlySpan<byte> payload) {
        if (payload.Length < 10) {
            return null;
        }
        int id = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int type = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        ReadOnlySpan<byte> body = payload[8..];
        int end = body.IndexOf((byte)0);
        if (end < 0) {
            end = body.Length;
        }
        return new SourceRconPacket(id, type, Encoding.UTF8.GetString(body[..end]));
    }

    public static async Task WriteAsync(Stream stream, int id, int type, string body, CancellationToken cancellation) {
        byte[] packet = Encode(id, type, body);
        if (packet.Length > MaxPacketSize) {
            throw new RconException("The command is too long for RCON.");
        }
        await stream.WriteAsync(packet, cancellation);
    }

    public static async Task<SourceRconPacket> ReadAsync(Stream stream, CancellationToken cancellation) {
        byte[] sizeBytes = new byte[4];
        await stream.ReadExactlyAsync(sizeBytes, cancellation);
        int size = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
        if (size < 10 || size > MaxPacketSize * 4) {
            throw new IOException($"Not an RCON packet (size {size}).");
        }
        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellation);
        return Decode(payload) ?? throw new IOException("Not an RCON packet.");
    }
}

/// <summary>Splitting of RCON replies into console lines.</summary>
internal static class RconReply {
    /// <summary>Splits a reply into console lines without empty lines.</summary>
    public static IReadOnlyList<string> SplitLines(string reply) {
        return reply.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList();
    }
}
