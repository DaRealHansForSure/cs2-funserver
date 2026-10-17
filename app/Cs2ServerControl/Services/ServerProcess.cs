using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cs2ServerControl.Services;

/// <summary>One inbound firewall rule of the package; Exists is null when netsh could not tell.</summary>
public sealed record FirewallRuleState(string Name, string Protocol, int Port, bool? Exists);

/// <summary>
/// Starts and stops the CS2 dedicated server (cs2.exe), or attaches to a
/// cs2.exe of this package that is already running (started by hand or by an
/// earlier window).
/// </summary>
public sealed class ServerProcess {
    public const int GamePort = 27015;
    public const int DefaultMaxPlayers = 20;
    public const int MaxPlayersLimit = 32;
    public const int TvPort = 27020;

    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(10);

    // CS2 players and CSTV only talk UDP. RCON is TCP on the game port, but
    // only this window uses it, so that port stays closed to the LAN. Maps
    // come from Steam, there is no download server.
    internal static readonly (string Name, string Protocol, int Port)[] FirewallRules = [
        ("CS2 LAN Server (UDP)", "UDP", GamePort),
        ("CS2 LAN TV (CSTV)", "UDP", TvPort),
    ];

    private readonly PackagePaths _paths;

    // The nav build helper is the same hlds.exe, so the path alone does not
    // tell which one is the game server. The command line does, but reading
    // it goes through WMI, so every process id is judged only once.
    private readonly Dictionary<int, bool> _isGameServerById = [];

    public ServerProcess(PackagePaths paths) {
        _paths = paths;
    }

    public bool IsRunning => FindHlds() != null;

    /// <summary>When hlds.exe was started, also for a server started outside this window.</summary>
    public DateTime? StartedAt {
        get {
            try {
                return FindHlds()?.StartTime;
            } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) {
                return null;
            }
        }
    }

    /// <summary>Every IPv4 address of this PC the others could reach, for the Options screen.</summary>
    public static IReadOnlyList<IPAddress> ListLanAddresses() {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address)
                && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    /// <summary>The chosen address when it still exists on this PC, else the automatic one.</summary>
    public static IPAddress ResolveLanAddress(string? chosenAddress) {
        bool isChosen = IPAddress.TryParse(chosenAddress, out IPAddress? chosen);
        if (isChosen && ListLanAddresses().Contains(chosen!)) {
            return chosen!;
        }
        return FindLanAddress();
    }

    public static IPAddress FindLanAddress() {
        // The adapter with a default gateway is the one connected to the
        // router, which is the address the other PCs can reach.
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces()) {
            if (adapter.OperationalStatus != OperationalStatus.Up) {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            if (!hasGateway) {
                continue;
            }

            UnicastIPAddressInformation? address = properties.UnicastAddresses
                .FirstOrDefault(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork);
            if (address != null) {
                return address.Address;
            }
        }

        IPAddress? anyAddress = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address)
                && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        return anyAddress ?? IPAddress.Loopback;
    }

    public void Start(string modeId, string startMap, int maxPlayers, IPAddress lanAddress, string rconPassword) {
        if (!_paths.IsServerInstalled) {
            throw new InvalidOperationException("The server is not installed yet. Run Install or repair on the Install screen first.");
        }
        if (IsRunning) {
            throw new InvalidOperationException("The server is already running.");
        }

        WriteLocalConfigPassword(rconPassword);
        WriteActiveMode(modeId);
        // A CS2 update replaces gameinfo.gi and with it the line that loads Metamod.
        Install.ServerComponentsInstaller.EnsureMetamodSearchPath(new Install.InstallPaths(_paths), log: null);

        GameMode mode = GameModes.Find(modeId) ?? GameModes.All[0];
        string arguments = BuildArguments(mode, startMap, maxPlayers, lanAddress, rconPassword);

        Process? server = Process.Start(new ProcessStartInfo {
            FileName = _paths.Cs2Exe,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(_paths.Cs2Exe)!,
            WindowStyle = ProcessWindowStyle.Minimized,
            UseShellExecute = true,
        });
        if (server != null) {
            RememberProcess(server.Id, isGameServer: true);
        }
    }

    /// <summary>
    /// The cs2.exe command line. -usercon turns on RCON (TCP on the game port),
    /// sv_lan keeps it a LAN server that needs no Steam game server token.
    /// An empty server hibernates and then runs no frames at all, so the
    /// plugin would neither run the mode configs nor send the live feed
    /// while only bots play. fun/local.cfg runs once here; on every map the
    /// mode plugin runs it again after the mode config.
    /// </summary>
    internal static string BuildArguments(GameMode mode, string startMap, int maxPlayers, IPAddress lanAddress, string rconPassword) {
        return string.Join(' ',
            "-dedicated -console -usercon -condebug",
            $"-port {GamePort}",
            $"-ip {lanAddress}",
            $"-maxplayers {maxPlayers}",
            "+sv_lan 1",
            "+sv_hibernate_when_empty 0",
            $"+game_type {mode.GameType}",
            $"+game_mode {mode.GameModeNumber}",
            $"+rcon_password {rconPassword}",
            "+exec fun/local.cfg",
            $"+map {startMap}");
    }

    /// <summary>
    /// CS2 has no localinfo, so the active fun mode goes into a small file our
    /// mode plugin reads on every map start (cfg\fun\active_mode.txt).
    /// </summary>
    public void WriteActiveMode(string modeId) {
        Directory.CreateDirectory(_paths.FunConfigDir);
        File.WriteAllText(_paths.ActiveModeFile, modeId, System.Text.Encoding.ASCII);
    }

    /// <summary>Waits for hlds.exe to end after "quit", then kills what is left.</summary>
    public async Task WaitForExitOrKillAsync() {
        Process? hlds = FindHlds();
        if (hlds != null) {
            using var timeout = new CancellationTokenSource(QuitTimeout);
            try {
                await hlds.WaitForExitAsync(timeout.Token);
            } catch (OperationCanceledException) {
                TryKill(hlds);
            }
        }
    }

    /// <summary>
    /// The first map of the mode's cycle that is installed. Null when the mode
    /// has no installed map at all.
    /// </summary>
    public string? FirstMapOfMode(string modeId) {
        return MapCycleReader.Read(_paths.MapCycleFile(modeId))
            .FirstOrDefault(map => File.Exists(_paths.MapFile(map)));
    }

    public static bool AreFirewallRulesMissing() {
        return ReadFirewallRules().Any(rule => rule.Exists == false);
    }

    /// <summary>
    /// Every inbound rule this package needs and whether Windows has it. Null
    /// for Exists when netsh could not be asked. Blocks for a moment per rule.
    /// </summary>
    public static IReadOnlyList<FirewallRuleState> ReadFirewallRules() {
        return FirewallRules.Select(rule => new FirewallRuleState(rule.Name, rule.Protocol, rule.Port, RuleExists(rule.Name))).ToList();
    }

    private static bool? RuleExists(string name) {
        try {
            using Process? check = Process.Start(new ProcessStartInfo {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{name}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (check == null) {
                return null;
            }
            check.StandardOutput.ReadToEnd();
            check.WaitForExit();
            return check.ExitCode == 0;
        } catch (Win32Exception) {
            return null;
        }
    }

    /// <summary>
    /// Adds the inbound rules through an elevated cmd. The live feed port is
    /// left closed on purpose: it only ever talks to this PC. Each rule is
    /// deleted before it is added, so rules that already exist are not doubled.
    /// </summary>
    public static async Task<bool> AddFirewallRulesAsync() {
        IEnumerable<string> commands = FirewallRules.SelectMany(rule => new[] {
            $"netsh advfirewall firewall delete rule name=\"{rule.Name}\" >nul",
            $"netsh advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow protocol={rule.Protocol} localport={rule.Port} >nul",
        });
        try {
            using Process? elevated = Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = "/c " + string.Join(" & ", commands),
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (elevated == null) {
                return false;
            }
            await elevated.WaitForExitAsync();
            return true;
        } catch (Win32Exception) {
            // The user said no to the UAC prompt; Windows will ask on its own later.
            return false;
        }
    }

    /// <summary>
    /// fun_modes runs local.cfg after the mode configs on every map start, so
    /// its rcon_password would win over the command line. Keeping both the
    /// same also lets a server started outside this window accept this app.
    /// </summary>
    public void WriteLocalConfigPassword(string rconPassword) {
        var settings = new LocalSettings(_paths.LocalConfigFile);
        settings.Set("rcon_password", rconPassword);
        settings.Save();
    }

    /// <summary>The password in local.cfg, used by a server started outside this window.</summary>
    public string? ReadLocalConfigPassword() {
        return new LocalSettings(_paths.LocalConfigFile).Get("rcon_password");
    }

    /// <summary>The game server: an hlds.exe of this package that is not the nav build helper.</summary>
    private Process? FindHlds() {
        Process[] running = Process.GetProcessesByName("cs2");
        lock (_isGameServerById) {
            ForgetEndedProcesses(running);
        }

        foreach (Process process in running) {
            if (IsOurHlds(process) && IsGameServer(process.Id)) {
                return process;
            }
        }
        return null;
    }

    private bool IsOurHlds(Process process) {
        try {
            string? path = process.MainModule?.FileName;
            return path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_paths.Cs2Exe), StringComparison.OrdinalIgnoreCase);
        } catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) {
            // A process that ended or belongs to another user is not ours.
            return false;
        }
    }

    private bool IsGameServer(int processId) {
        lock (_isGameServerById) {
            if (_isGameServerById.TryGetValue(processId, out bool known)) {
                return known;
            }
        }

        string? commandLine = ReadCommandLine(processId);
        if (commandLine == null) {
            // Ended in between, or WMI is unavailable. Judge it again next time
            // instead of remembering a guess.
            return true;
        }
        bool isGameServer = !IsNavHelperCommandLine(commandLine);
        RememberProcess(processId, isGameServer);
        return isGameServer;
    }

    /// <summary>
    /// Ends nav build helpers of this package. Only the window starts them, so
    /// one that runs while the window opens or closes is left over: it sits
    /// on the invisible desktop and would run forever otherwise.
    /// </summary>
    public void StopNavHelpers() {
        foreach (Process process in Process.GetProcessesByName("cs2")) {
            if (!IsOurHlds(process)) {
                continue;
            }
            string? commandLine = ReadCommandLine(process.Id);
            if (commandLine != null && IsNavHelperCommandLine(commandLine)) {
                TryKill(process);
            }
        }
    }

    internal static bool IsNavHelperCommandLine(string commandLine) {
        return commandLine.Contains($"-port {NavBuilder.HelperPort}", StringComparison.OrdinalIgnoreCase);
    }

    private void RememberProcess(int processId, bool isGameServer) {
        lock (_isGameServerById) {
            _isGameServerById[processId] = isGameServer;
        }
    }

    private void ForgetEndedProcesses(Process[] running) {
        // Windows reuses process ids, an old verdict must not stick to a new process.
        var runningIds = running.Select(process => process.Id).ToHashSet();
        foreach (int processId in _isGameServerById.Keys.Where(id => !runningIds.Contains(id)).ToList()) {
            _isGameServerById.Remove(processId);
        }
    }

    private static string? ReadCommandLine(int processId) {
        try {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (System.Management.ManagementBaseObject process in searcher.Get()) {
                using (process) {
                    return process["CommandLine"] as string;
                }
            }
        } catch (Exception exception) when (exception is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) {
            // Falls through to "unknown".
        }
        return null;
    }

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) {
            // Already gone.
        }
    }
}
