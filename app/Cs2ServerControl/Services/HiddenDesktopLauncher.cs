using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cs2ServerControl.Services;

/// <summary>
/// Starts a program on a separate, never shown Windows desktop. hlds.exe
/// -console opens its own console window whatever start options it gets,
/// and that window takes the focus; on another desktop it can never show up
/// on the owner's screen or pull them out of a game.
/// </summary>
internal static class HiddenDesktopLauncher {
    private const string DesktopName = "Cs2ServerControlHidden";
    private const uint DesktopAllAccess = 0x01FF;
    private const uint CreateNewConsole = 0x00000010;
    private const uint UseShowWindow = 0x00000001;
    private const short ShowMinimizedNoActivate = 7;

    private static readonly object CreateLock = new();
    private static IntPtr _desktop;

    public static Process Start(string fileName, string arguments, string workingDirectory) {
        EnsureDesktop();

        var startup = new StartupInfo {
            Size = Marshal.SizeOf<StartupInfo>(),
            Desktop = DesktopName,
            Flags = UseShowWindow,
            ShowWindow = ShowMinimizedNoActivate,
        };
        string commandLine = $"\"{fileName}\" {arguments}";
        bool started = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNewConsole,
            IntPtr.Zero, workingDirectory, ref startup, out ProcessInformation info);
        if (!started) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try {
            return Process.GetProcessById(info.ProcessId);
        } finally {
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    private static void EnsureDesktop() {
        lock (CreateLock) {
            if (_desktop != IntPtr.Zero) {
                return;
            }
            // Kept open for the app's lifetime: a desktop disappears with its last handle.
            _desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
            if (_desktop == IntPtr.Zero) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int CharsWidth;
        public int CharsHeight;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string desktop, IntPtr device, IntPtr deviceMode, uint flags, uint access, IntPtr attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
