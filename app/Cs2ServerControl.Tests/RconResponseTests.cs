using System.IO;
using System.Net;
using System.Net.Sockets;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class RconResponseTests {
    [Fact]
    public void EncodesAPacketLikeTheSourceProtocol() {
        byte[] packet = SourceRconPacket.Encode(7, SourceRconPacket.ExecCommand, "status");

        // size (4) + id (4) + type (4) + "status" (6) + two zero bytes
        Assert.Equal(4 + 4 + 4 + 6 + 2, packet.Length);
        Assert.Equal(packet.Length - 4, BitConverter.ToInt32(packet, 0));
        Assert.Equal(7, BitConverter.ToInt32(packet, 4));
        Assert.Equal(2, BitConverter.ToInt32(packet, 8));
        Assert.Equal(0, packet[^1]);
        Assert.Equal(0, packet[^2]);
    }

    [Fact]
    public void DecodesWhatItEncodes() {
        byte[] packet = SourceRconPacket.Encode(42, SourceRconPacket.ResponseValue, "hostname: LAN Fun");

        SourceRconPacket? decoded = SourceRconPacket.Decode(packet.AsSpan(4));

        Assert.Equal(new SourceRconPacket(42, SourceRconPacket.ResponseValue, "hostname: LAN Fun"), decoded);
        Assert.Null(SourceRconPacket.Decode([1, 2, 3]));
    }

    [Fact]
    public void SplitsRepliesIntoLines() {
        Assert.Equal(["a", "b"], RconReply.SplitLines("a\r\n\r\nb  \n"));
    }

    [Fact]
    public async Task LogsInAndJoinsAnAnswerThatComesInSeveralPackets() {
        using var server = new FakeRconServer("secret", ["first part, ", "second part"]);
        var client = new RconClient { Server = server.EndPoint, Password = "secret" };

        string answer = await client.SendAsync("status");

        Assert.Equal("first part, second part", answer);
        Assert.True(client.IsConnected);
        Assert.Equal("status", await server.LastCommand);
    }

    [Fact]
    public async Task AWrongPasswordIsReported() {
        using var server = new FakeRconServer("secret", ["never sent"]);
        var client = new RconClient { Server = server.EndPoint, Password = "wrong" };

        RconException failure = await Assert.ThrowsAsync<RconException>(() => client.SendAsync("status"));

        Assert.Contains("refused the RCON password", failure.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task NoServerMeansNoAnswer() {
        // A port nobody listens on: take a free one and close it again.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        var client = new RconClient { Server = endpoint, Password = "secret" };

        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("status"));
    }

    /// <summary>
    /// Answers like a CS2 server: an empty value packet and the login answer,
    /// then each command in several packets and every mirror packet as is.
    /// </summary>
    private sealed class FakeRconServer : IDisposable {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _lastCommand = new();

        public FakeRconServer(string password, IReadOnlyList<string> answerParts) {
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _ = ServeAsync(password, answerParts);
        }

        public IPEndPoint EndPoint { get; }
        public Task<string> LastCommand => _lastCommand.Task;

        public void Dispose() {
            _listener.Stop();
        }

        private async Task ServeAsync(string password, IReadOnlyList<string> answerParts) {
            try {
                using TcpClient connection = await _listener.AcceptTcpClientAsync();
                NetworkStream stream = connection.GetStream();
                while (true) {
                    SourceRconPacket request = await SourceRconPacket.ReadAsync(stream, CancellationToken.None);
                    if (request.Type == SourceRconPacket.Auth) {
                        await SourceRconPacket.WriteAsync(stream, request.Id, SourceRconPacket.ResponseValue, "", CancellationToken.None);
                        int answerId = request.Body == password ? request.Id : SourceRconPacket.LoginFailedId;
                        await SourceRconPacket.WriteAsync(stream, answerId, SourceRconPacket.AuthResponse, "", CancellationToken.None);
                        continue;
                    }
                    if (request.Type == SourceRconPacket.ExecCommand) {
                        _lastCommand.TrySetResult(request.Body);
                        foreach (string part in answerParts) {
                            await SourceRconPacket.WriteAsync(stream, request.Id, SourceRconPacket.ResponseValue, part, CancellationToken.None);
                        }
                        continue;
                    }
                    await SourceRconPacket.WriteAsync(stream, request.Id, SourceRconPacket.ResponseValue, "", CancellationToken.None);
                }
            } catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) {
                // The client hung up or the test is over.
            }
        }
    }
}
