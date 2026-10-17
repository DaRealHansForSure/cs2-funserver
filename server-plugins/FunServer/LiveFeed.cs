using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace FunServer;

/// <summary>
/// Streams the game to the Server Control window over UDP on this PC, in the
/// same text format as the CS 1.6 plugin fun_livemap, so the window's parser
/// stays the same:
///   CSLIVE1|S|map|mode|roundSecondsLeft|maxPlayers, then one P line per player
///   CSLIVE1|K|killerUserid|victimUserid|weapon|headshot
///   CSLIVE1|T, then one line per bullet: shooter|sx|sy|sz|ex|ey|ez|hitPlayer
/// The feed only goes to 127.0.0.1; the window opens no firewall port for it.
/// </summary>
public sealed partial class FunServerPlugin {
    private const string PacketPrefix = "CSLIVE1";
    private const int FeedPort = 27090;
    private const float SnapshotInterval = 0.2f;
    private const int MaxShotsPerPacket = 80;
    private const int TeamTerrorist = 2;
    private const int TeamCounterTerrorist = 3;

    private readonly IPEndPoint _feedTarget = new(IPAddress.Loopback, FeedPort);
    private readonly StringBuilder _shots = new();
    private UdpClient? _feed;
    private int _shotCount;
    private float _roundStartedAt = -1;

    private void LoadLiveFeed() {
        _feed = new UdpClient();
        AddTimer(SnapshotInterval, SendSnapshot, TimerFlags.REPEAT);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        RegisterEventHandler<EventRoundStart>((_, _) => {
            _roundStartedAt = Server.CurrentTime;
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnMapStart>(_ => _roundStartedAt = -1);
    }

    private void UnloadLiveFeed() {
        _feed?.Dispose();
        _feed = null;
    }

    private void SendSnapshot() {
        if (_feed == null) {
            return;
        }
        SendShots();

        var packet = new StringBuilder();
        packet.Append(CultureInfo.InvariantCulture, $"{PacketPrefix}|S|{Server.MapName}|{ReadActiveMode()}|{RoundSecondsLeft()}|{Server.MaxPlayers}");
        foreach (CCSPlayerController player in Utilities.GetPlayers()) {
            if (!player.IsValid || player.IsHLTV) {
                continue;
            }
            AppendPlayerLine(packet, player);
        }
        Send(packet.ToString());
    }

    private void AppendPlayerLine(StringBuilder packet, CCSPlayerController player) {
        CCSPlayerPawn? pawn = player.PlayerPawn.Value;
        bool isAlive = player.PawnIsAlive && pawn != null;
        var origin = pawn?.AbsOrigin;
        float yaw = pawn?.EyeAngles?.Y ?? 0;
        int health = isAlive ? pawn!.Health : 0;
        string weapon = isAlive ? ShortWeaponName(pawn!.WeaponServices?.ActiveWeapon.Value?.DesignerName) : "";
        var stats = player.ActionTrackingServices?.MatchStats;

        // The separators must never show up inside a field.
        string name = player.PlayerName.Replace('|', '/').Replace('\n', '/').Replace('\r', '/');
        string line = string.Create(CultureInfo.InvariantCulture,
            $"\nP|{player.UserId ?? 0}|{name}|{TeamName(player.TeamNum)}|{(isAlive ? 1 : 0)}|{(player.IsBot ? 1 : 0)}|{health}|{Round(origin?.X)}|{Round(origin?.Y)}|{Round(origin?.Z)}|{Round(yaw)}|{stats?.Kills ?? 0}|{stats?.Deaths ?? 0}|{weapon}|{HookField(player)}|{GlowField(isAlive ? pawn : null)}");
        packet.Append(line);
    }

    /// <summary>Hooking 1 and the point the rope holds, or 0 0 0 0.</summary>
    private string HookField(CCSPlayerController player) {
        Vector? point = HookPointOf(player);
        if (point == null) {
            return "0|0|0|0";
        }
        return string.Create(CultureInfo.InvariantCulture, $"1|{Round(point.X)}|{Round(point.Y)}|{Round(point.Z)}");
    }

    /// <summary>
    /// The tint fun_glow set, read back from the pawn, so the Live Map shows
    /// what the players really see. White is the untinted default: -1 -1 -1.
    /// </summary>
    private static string GlowField(CCSPlayerPawn? pawn) {
        if (pawn == null) {
            return "-1|-1|-1";
        }
        Color render = pawn.Render;
        bool isUntinted = render.R == 255 && render.G == 255 && render.B == 255;
        if (isUntinted) {
            return "-1|-1|-1";
        }
        return string.Create(CultureInfo.InvariantCulture, $"{render.R}|{render.G}|{render.B}");
    }

    private HookResult OnPlayerDeath(EventPlayerDeath death, GameEventInfo info) {
        int killer = death.Attacker?.UserId ?? 0;
        int victim = death.Userid?.UserId ?? 0;
        Send(string.Create(CultureInfo.InvariantCulture, $"{PacketPrefix}|K|{killer}|{victim}|{death.Weapon}|{(death.Headshot ? 1 : 0)}"));
        return HookResult.Continue;
    }

    /// <summary>
    /// CS2 reports every bullet impact with its point; the shot starts at the
    /// shooter's eyes. A hit on a player shows up as player_hurt, not here, so
    /// the hit flag stays 0 for now.
    /// </summary>
    private HookResult OnBulletImpact(EventBulletImpact impact, GameEventInfo info) {
        CCSPlayerController? shooter = impact.Userid;
        CCSPlayerPawn? pawn = shooter?.PlayerPawn.Value;
        if (shooter == null || pawn?.AbsOrigin == null || _shotCount >= MaxShotsPerPacket) {
            return HookResult.Continue;
        }
        float eyeHeight = pawn.ViewOffset.Z;
        _shots.Append(CultureInfo.InvariantCulture,
            $"\n{shooter.UserId ?? 0}|{Round(pawn.AbsOrigin.X)}|{Round(pawn.AbsOrigin.Y)}|{Round(pawn.AbsOrigin.Z + eyeHeight)}" +
            $"|{Round(impact.X)}|{Round(impact.Y)}|{Round(impact.Z)}|0");
        _shotCount++;
        return HookResult.Continue;
    }

    private void SendShots() {
        if (_shotCount == 0) {
            return;
        }
        Send($"{PacketPrefix}|T{_shots}");
        _shots.Clear();
        _shotCount = 0;
    }

    private int RoundSecondsLeft() {
        if (_roundStartedAt < 0) {
            return -1;
        }
        float roundSeconds = (ConVar.Find("mp_roundtime")?.GetPrimitiveValue<float>() ?? 0) * 60f;
        int left = (int)MathF.Ceiling(roundSeconds - (Server.CurrentTime - _roundStartedAt));
        return Math.Max(left, 0);
    }

    private void Send(string text) {
        if (_feed == null) {
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try {
            _feed.Send(bytes, bytes.Length, _feedTarget);
        } catch (SocketException) {
            // Nobody listens while the window is closed; the next packet tries again.
        }
    }

    private static string TeamName(int team) {
        if (team == TeamTerrorist) {
            return "T";
        }
        if (team == TeamCounterTerrorist) {
            return "CT";
        }
        return "SPEC";
    }

    /// <summary>"weapon_ak47" becomes "ak47", like the kill feed names.</summary>
    private static string ShortWeaponName(string? designerName) {
        if (string.IsNullOrEmpty(designerName)) {
            return "";
        }
        const string prefix = "weapon_";
        return designerName.StartsWith(prefix, StringComparison.Ordinal) ? designerName[prefix.Length..] : designerName;
    }

    private static string Round(float? value) {
        return ((int)MathF.Round(value ?? 0)).ToString(CultureInfo.InvariantCulture);
    }
}
