using System.ComponentModel;
using System.Diagnostics;

namespace Cs2ServerControl.Install;

/// <summary>
/// Runs a console tool without any window and hands each output line to the
/// caller. The owner plays on this PC, a console that takes the focus in the
/// middle of a game is not acceptable.
/// </summary>
internal static class HiddenProcess {
    public static async Task<int> RunAsync(string exe, string arguments, string workingDir, Action<string> onLine, CancellationToken cancellation) {
        using var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            },
        };
        process.OutputDataReceived += (_, line) => ForwardLine(line.Data, onLine);
        process.ErrorDataReceived += (_, line) => ForwardLine(line.Data, onLine);

        process.Start();
        // A tool that asks something would wait forever on an open input.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try {
            await process.WaitForExitAsync(cancellation);
        } catch (OperationCanceledException) {
            TryKill(process);
            throw;
        }
        return process.ExitCode;
    }

    private static void ForwardLine(string? line, Action<string> onLine) {
        if (string.IsNullOrWhiteSpace(line)) {
            return;
        }
        onLine(line.TrimEnd());
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
