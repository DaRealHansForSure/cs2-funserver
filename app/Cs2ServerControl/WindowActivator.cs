using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cs2ServerControl;

/// <summary>Brings the already open window to the front, for a second start of the exe.</summary>
internal static class WindowActivator {
    private const int Restore = 9;

    public static void ActivateOtherInstance() {
        using Process current = Process.GetCurrentProcess();
        foreach (Process other in Process.GetProcessesByName(current.ProcessName)) {
            using (other) {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero) {
                    continue;
                }
                // The owner started this on purpose, so taking the focus is wanted here.
                ShowWindow(other.MainWindowHandle, Restore);
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
