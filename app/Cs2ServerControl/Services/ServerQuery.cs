using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cs2ServerControl.Services;

public sealed record ServerInfo(string Name, string Map, int Players, int MaxPlayers, int Bots);

/// <summary>
/// The public server query the game's LAN browser uses (A2S_INFO, A2S_RULES).
/// The status polling runs on this instead of RCON, because the engine writes
/// every RCON command, password included, into the game log files.
/// </summary>
public sealed class ServerQuery {
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SplitPacketWindow = TimeSpan.FromMilliseconds(150);

    public async Task<ServerInfo?> QueryInfoAsync(IPEndPoint server, CancellationToken cancellation = default) {
        byte[] request = [0xFF, 0xFF, 0xFF, 0xFF, .. Encoding.ASCII.GetBytes("TSource Engine Query\0")];
        byte[]? reply = await ExchangeWithChallengeAsync(server, request, appendChallenge: true, cancellation);
        return reply == null ? null : QueryParser.ParseInfo(reply);
    }

    /// <summary>Server cvars flagged for the browser, amx_nextmap and amx_timeleft among them.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> QueryRulesAsync(IPEndPoint server, CancellationToken cancellation = default) {
        byte[] request = [0xFF, 0xFF, 0xFF, 0xFF, (byte)'V', 0xFF, 0xFF, 0xFF, 0xFF];
        byte[]? reply = await ExchangeWithChallengeAsync(server, request, appendChallenge: false, cancellation);
        return reply == null ? null : QueryParser.ParseRules(reply);
    }

    private static async Task<byte[]?> ExchangeWithChallengeAsync(IPEndPoint server, byte[] request, bool appendChallenge, CancellationToken cancellation) {
        using var client = new UdpClient(server.AddressFamily);
        try {
            byte[]? reply = await ExchangeAsync(client, server, request, cancellation);
            if (reply == null || !QueryParser.TryReadChallenge(reply, out byte[] challenge)) {
                return reply;
            }

            // The server wants its challenge number back: appended for
            // A2S_INFO, in place of the -1 placeholder for A2S_RULES.
            byte[] retry = appendChallenge ? [.. request, .. challenge] : [.. request[..5], .. challenge];
            return await ExchangeAsync(client, server, retry, cancellation);
        } catch (SocketException) {
            return null;
        }
    }

    private static async Task<byte[]?> ExchangeAsync(UdpClient client, IPEndPoint server, byte[] request, CancellationToken cancellation) {
        await client.SendAsync(request, server, cancellation);

        var parts = new SortedDictionary<int, byte[]>();
        int expectedParts = 1;
        TimeSpan waitFor = ReplyTimeout;
        while (true) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(waitFor);
            UdpReceiveResult result;
            try {
                result = await client.ReceiveAsync(timeout.Token);
            } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
                return null;
            }

            if (!QueryParser.TryReadSplitPart(result.Buffer, out int index, out int total, out byte[] payload)) {
                return result.Buffer;
            }

            // A long rules list arrives in several numbered packets.
            parts[index] = payload;
            expectedParts = total;
            waitFor = SplitPacketWindow;
            if (parts.Count >= expectedParts) {
                return parts.Values.SelectMany(part => part).ToArray();
            }
        }
    }
}

/// <summary>Parsing of query answers, separate so the tests can feed captured packets.</summary>
internal static class QueryParser {
    private const byte ChallengeHeader = (byte)'A';
    private const byte SourceInfoHeader = (byte)'I';
    private const byte GoldSrcInfoHeader = (byte)'m';
    private const byte RulesHeader = (byte)'E';

    public static bool TryReadChallenge(byte[] packet, out byte[] challenge) {
        challenge = [];
        if (packet.Length < 9 || !HasSimpleHeader(packet) || packet[4] != ChallengeHeader) {
            return false;
        }
        challenge = packet[5..9];
        return true;
    }

    /// <summary>GoldSrc split packet: FE FF FF FF, request id, (index &lt;&lt; 4 | total), payload.</summary>
    public static bool TryReadSplitPart(byte[] packet, out int index, out int total, out byte[] payload) {
        index = 0;
        total = 0;
        payload = [];
        bool isSplit = packet.Length > 9 && packet[0] == 0xFE && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF;
        if (!isSplit) {
            return false;
        }
        total = packet[8] & 0x0F;
        index = packet[8] >> 4;
        payload = packet[9..];
        return total > 0;
    }

    public static ServerInfo? ParseInfo(byte[] packet) {
        if (packet.Length < 6 || !HasSimpleHeader(packet)) {
            return null;
        }

        var reader = new PacketReader(packet, 5);
        switch (packet[4]) {
            case SourceInfoHeader: {
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
            case GoldSrcInfoHeader: {
                reader.ReadString();
                string name = reader.ReadString();
                string map = reader.ReadString();
                reader.ReadString();
                reader.ReadString();
                int players = reader.ReadByte();
                int maxPlayers = reader.ReadByte();
                return new ServerInfo(name, map, players, maxPlayers, 0);
            }
            default:
                return null;
        }
    }

    public static IReadOnlyDictionary<string, string>? ParseRules(byte[] packet) {
        if (packet.Length < 7 || !HasSimpleHeader(packet) || packet[4] != RulesHeader) {
            return null;
        }

        var reader = new PacketReader(packet, 5);
        int count = reader.ReadShort();
        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < count && !reader.IsAtEnd; index++) {
            string name = reader.ReadString();
            string value = reader.ReadString();
            rules[name] = value;
        }
        return rules;
    }

    private static bool HasSimpleHeader(byte[] packet) {
        return packet[0] == 0xFF && packet[1] == 0xFF && packet[2] == 0xFF && packet[3] == 0xFF;
    }

    private sealed class PacketReader(byte[] data, int position) {
        private int _position = position;

        public bool IsAtEnd => _position >= data.Length;

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
