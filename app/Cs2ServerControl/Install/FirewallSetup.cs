using System.ComponentModel;
using System.Diagnostics;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

/// <summary>
/// The inbound Windows Firewall rules for the game, fast download and HLTV
/// ports, the same rules <see cref="ServerProcess.AddFirewallRulesAsync"/> adds.
/// The app is not elevated, so missing rules go through one elevated netsh
/// run; when every rule is there, no UAC prompt shows up at all.
/// </summary>
internal sealed class FirewallSetup {
    private static readonly (string Name, string Protocol, int Port)[] Rules = [
        ("CS16 LAN Server (UDP)", "UDP", ServerProcess.GamePort),
        ("CS16 LAN Server (TCP)", "TCP", ServerProcess.GamePort),
        ("CS16 LAN Fast-Download", "TCP", ServerProcess.FastDownloadPort),
        ("CS16 LAN TV (HLTV)", "UDP", ServerProcess.TvPort),
    ];

    private readonly InstallLog _log;

    public FirewallSetup(InstallLog log) {
        _log = log;
    }

    public async Task AddRulesAsync(CancellationToken cancellation) {
        _log.Step("Windows Firewall");

        List<(string Name, string Protocol, int Port)> missingRules = [];
        foreach ((string Name, string Protocol, int Port) rule in Rules) {
            if (!await RuleExistsAsync(rule.Name, cancellation)) {
                missingRules.Add(rule);
            }
        }
        if (missingRules.Count == 0) {
            return;
        }

        bool elevated = await RunElevatedAsync(missingRules, cancellation);
        if (!elevated) {
            _log.Note("Not running as administrator. Windows will ask on the first server start,");
            _log.Note("then click \"Allow access\" for private networks.");
            return;
        }

        foreach ((string name, _, _) in missingRules) {
            if (await RuleExistsAsync(name, cancellation)) {
                _log.Note($"{name} allowed");
            } else {
                _log.Problem($"{name} could not be added.");
            }
        }
    }

    private static async Task<bool> RuleExistsAsync(string name, CancellationToken cancellation) {
        try {
            int exitCode = await HiddenProcess.RunAsync("netsh", $"advfirewall firewall show rule name=\"{name}\"",
                Environment.SystemDirectory, _ => { }, cancellation);
            return exitCode == 0;
        } catch (Win32Exception) {
            // Without netsh there is nothing to check; asking for UAC would not help either.
            return true;
        }
    }

    /// <summary>
    /// One elevated cmd for all missing rules, so the owner sees at most one
    /// UAC prompt. A rule is deleted before it is added, so a half finished
    /// earlier run never leaves duplicates. False when the prompt was declined.
    /// </summary>
    private static async Task<bool> RunElevatedAsync(List<(string Name, string Protocol, int Port)> rules, CancellationToken cancellation) {
        IEnumerable<string> commands = rules.SelectMany(rule => new[] {
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
            await elevated.WaitForExitAsync(cancellation);
            return true;
        } catch (Win32Exception) {
            // The owner said no to the UAC prompt.
            return false;
        }
    }
}
