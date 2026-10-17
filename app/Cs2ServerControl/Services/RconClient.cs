using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

public sealed class RconException(string message) : Exception(message);

/// <summary>
/// GoldSrc RCON over UDP: "challenge rcon" for a number, then
/// "rcon number "password" command". Long answers come in several packets,
/// so everything that arrives within 150 ms after the first packet is joined.
/// One call at a time, because the challenge belongs to one socket exchange.
/// </summary>
public sealed class RconClient {
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(2);
    // Commands without console output (setting a cvar) get no reply packet at
    // all, so a quiet server after a valid challenge means "done", not "gone".
    private static readonly TimeSpan CommandReplyWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CollectWindow = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private string? _challenge;

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
            using var client = new UdpClient(Server.AddressFamily);
            _challenge ??= await RequestChallengeAsync(client, cancellation);

            string answer = await ExecuteAsync(client, command, cancellation);
            if (RconReply.IsBadChallenge(answer)) {
                // The challenge changes when the server restarts, ask once more.
                _challenge = await RequestChallengeAsync(client, cancellation);
                answer = await ExecuteAsync(client, command, cancellation);
            }
            if (RconReply.IsBadPassword(answer)) {
                throw new RconException("The server refused the RCON password. Restart the server from this window.");
            }
            SetConnected(true);
            return answer.TrimEnd();
        } catch (SocketException exception) {
            _challenge = null;
            SetConnected(false);
            throw new RconException($"The server does not answer ({exception.SocketErrorCode}).");
        } catch (RconException) {
            _challenge = null;
            SetConnected(false);
            throw;
        } finally {
            _oneAtATime.Release();
        }
    }

    /// <summary>Forgets the challenge, for example after the server restarted.</summary>
    public void Reset() {
        _challenge = null;
        SetConnected(false);
    }

    private void SetConnected(bool isConnected) {
        if (IsConnected == isConnected) {
            return;
        }
        IsConnected = isConnected;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> RequestChallengeAsync(UdpClient client, CancellationToken cancellation) {
        await SendPacketAsync(client, "challenge rcon\n", cancellation);
        string reply = await ReceiveAllAsync(client, ChallengeTimeout, silenceIsAnswer: false, cancellation);

        string? challenge = RconReply.ReadChallenge(reply);
        if (challenge == null) {
            throw new RconException("The server does not answer RCON requests. Is it running?");
        }
        return challenge;
    }

    private async Task<string> ExecuteAsync(UdpClient client, string command, CancellationToken cancellation) {
        // A quote would end the password, the engine has no escaping for it.
        string password = Password.Replace("\"", "");
        await SendPacketAsync(client, $"rcon {_challenge} \"{password}\" {command}\n", cancellation);
        return await ReceiveAllAsync(client, CommandReplyWait, silenceIsAnswer: true, cancellation);
    }

    private async Task SendPacketAsync(UdpClient client, string text, CancellationToken cancellation) {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] packet = [0xFF, 0xFF, 0xFF, 0xFF, .. payload];
        await client.SendAsync(packet, Server!, cancellation);
    }

    private static async Task<string> ReceiveAllAsync(UdpClient client, TimeSpan firstWait, bool silenceIsAnswer, CancellationToken cancellation) {
        var answer = new StringBuilder();
        TimeSpan waitFor = firstWait;

        while (true) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(waitFor);
            try {
                UdpReceiveResult result = await client.ReceiveAsync(timeout.Token);
                answer.Append(RconReply.ReadPacketText(result.Buffer));
                waitFor = CollectWindow;
            } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
                if (answer.Length == 0 && !silenceIsAnswer) {
                    throw new RconException("The server did not answer within 2 seconds.");
                }
                return answer.ToString();
            }
        }
    }
}

/// <summary>Parsing of RCON replies, separate so the tests reach it without a socket.</summary>
internal static partial class RconReply {
    public static string? ReadChallenge(string reply) {
        Match match = ChallengePattern().Match(reply);
        if (!match.Success) {
            return null;
        }
        return match.Groups["number"].Value;
    }

    public static bool IsBadChallenge(string reply) {
        return reply.Contains("Bad challenge", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBadPassword(string reply) {
        return reply.Contains("Bad rcon_password", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the four 0xFF header bytes and the 'l' print marker the engine
    /// puts in front of console output, plus trailing zero bytes.
    /// </summary>
    public static string ReadPacketText(byte[] packet) {
        int start = 0;
        while (start < packet.Length && start < 4 && packet[start] == 0xFF) {
            start++;
        }
        if (start == 4 && start < packet.Length && packet[start] == (byte)'l') {
            start++;
        }

        int end = packet.Length;
        while (end > start && packet[end - 1] == 0) {
            end--;
        }
        return Encoding.UTF8.GetString(packet, start, end - start);
    }

    /// <summary>Splits a reply into console lines without empty lines.</summary>
    public static IReadOnlyList<string> SplitLines(string reply) {
        return reply.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList();
    }


    [GeneratedRegex(@"challenge rcon (?<number>\d+)")]
    private static partial Regex ChallengePattern();
}
