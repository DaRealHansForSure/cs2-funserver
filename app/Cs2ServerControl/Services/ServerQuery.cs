using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cs2ServerControl.Services;

public sealed record ServerInfo(string Name, string Map, int Players, int MaxPlayers, int Bots);

/// <summary>
/// The public server query the game's LAN browser uses (A2S_INFO). The
/// status polling runs on this instead of RCON, because the engine writes
/// every RCON command into its logs. CS2 answers no A2S_RULES; next map and
/// time left come with the live feed instead.
/// </summary>
public sealed class ServerQuery {
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(800);

    public async Task<ServerInfo?> QueryInfoAsync(IPEndPoint server, CancellationToken cancellation = default) {
        byte[] request = [0xFF, 0xFF, 0xFF, 0xFF, .. Encoding.ASCII.GetBytes("TSource Engine Query\0")];
        using var client = new UdpClient(server.AddressFamily);
        try {
            byte[]? reply = await ExchangeAsync(client, server, request, cancellation);
            if (reply != null && QueryParser.TryReadChallenge(reply, out byte[] challenge)) {
                // The server wants its challenge number back, appended to the request.
                reply = await ExchangeAsync(client, server, [.. request, .. challenge], cancellation);
            }
            return reply == null ? null : QueryParser.ParseInfo(reply);
        } catch (SocketException) {
            return null;
        }
    }

    private static async Task<byte[]?> ExchangeAsync(UdpClient client, IPEndPoint server, byte[] request, CancellationToken cancellation) {
        await client.SendAsync(request, server, cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReplyTimeout);
        try {
            UdpReceiveResult result = await client.ReceiveAsync(timeout.Token);
            return result.Buffer;
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
            return null;
        }
    }
}

/// <summary>Parsing of query answers, separate so the tests can feed captured packets.</summary>
internal static class QueryParser {
    private const byte ChallengeHeader = (byte)'A';
    private const byte InfoHeader = (byte)'I';

    public static bool TryReadChallenge(byte[] packet, out byte[] challenge) {
        challenge = [];
        if (packet.Length < 9 || !HasSimpleHeader(packet) || packet[4] != ChallengeHeader) {
            return false;
        }
        challenge = packet[5..9];
        return true;
    }

    public static ServerInfo? ParseInfo(byte[] packet) {
        if (packet.Length < 6 || !HasSimpleHeader(packet) || packet[4] != InfoHeader) {
            return null;
        }

        var reader = new PacketReader(packet, 5);
        reader.ReadByte();
        string name = reader.ReadString();
        string map = reader.ReadString();
        reader.ReadString();
        reader.ReadString();
        reader.ReadShort();
        int players = reader.ReadByte();
        int maxPlayers = reader.ReadByte();
        int bots = reader.ReadByte();
        return new ServerInfo(name, map, players, maxPlayers, bots);
    }

    private static bool HasSimpleHeader(byte[] packet) {
        return packet[0] == 0xFF && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF;
    }

    private sealed class PacketReader(byte[] data, int position) {
        private int _position = position;

        public int ReadByte() {
            if (_position >= data.Length) {
                return 0;
            }
            return data[_position++];
        }

        public int ReadShort() {
            int low = ReadByte();
            int high = ReadByte();
            return low | (high << 8);
        }

        public string ReadString() {
            int start = _position;
            while (_position < data.Length && data[_position] != 0) {
                _position++;
            }
            string text = Encoding.UTF8.GetString(data, start, _position - start);
            _position = Math.Min(_position + 1, data.Length);
            return text;
        }
    }
}
