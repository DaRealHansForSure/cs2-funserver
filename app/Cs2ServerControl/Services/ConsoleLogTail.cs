using System.IO;
using System.Text;

namespace Cs2ServerControl.Services;

/// <summary>
/// Follows the console.log that cs2.exe writes because of -condebug. The
/// output of cs2.exe -console lands in its own console window (on the hidden
/// desktop) and can not be read through stdout, so the log is the only way.
/// </summary>
public sealed class ConsoleLogTail : IDisposable {
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int InitialBacklogBytes = 8192;

    private readonly string _logFile;
    private readonly Timer _timer;
    private readonly StringBuilder _partialLine = new();
    private long _position;
    private int _isReading;
    private bool _skipCutLine;

    public ConsoleLogTail(string logFile) {
        _logFile = logFile;
        // Output from long before the app started is noise, only the tail is useful.
        _position = Math.Max(0, CurrentLength() - InitialBacklogBytes);
        // Starting inside the file lands in the middle of a line, which may be
        // the tail of an RCON echo with the password. That piece is dropped.
        _skipCutLine = _position > 0;
        _timer = new Timer(_ => ReadNewBytes(), null, TimeSpan.Zero, PollInterval);
    }

    /// <summary>Raised on a thread pool thread, with complete lines only.</summary>
    public event EventHandler<IReadOnlyList<string>>? LinesReceived;

    public void Dispose() {
        _timer.Dispose();
    }

    private long CurrentLength() {
        try {
            return File.Exists(_logFile) ? new FileInfo(_logFile).Length : 0;
        } catch (IOException) {
            return 0;
        }
    }

    private void ReadNewBytes() {
        // A slow disk must not stack up timer callbacks.
        if (Interlocked.Exchange(ref _isReading, 1) == 1) {
            return;
        }

        try {
            if (!File.Exists(_logFile)) {
                return;
            }

            using var stream = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position) {
                // A restarted server starts the log from scratch.
                _position = 0;
                _partialLine.Clear();
                _skipCutLine = false;
            }
            if (stream.Length == _position) {
                return;
            }

            stream.Seek(_position, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - _position];
            int read = stream.Read(buffer, 0, buffer.Length);
            _position += read;

            string newText = Encoding.UTF8.GetString(buffer, 0, read);
            if (_skipCutLine) {
                int firstBreak = newText.IndexOf('\n');
                if (firstBreak < 0) {
                    return;
                }
                newText = newText[(firstBreak + 1)..];
                _skipCutLine = false;
            }
            _partialLine.Append(newText);
            string text = _partialLine.ToString();
            int lastBreak = text.LastIndexOf('\n');
            if (lastBreak < 0) {
                return;
            }

            _partialLine.Clear();
            _partialLine.Append(text[(lastBreak + 1)..]);
            List<string> lines = text[..lastBreak]
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count > 0) {
                LinesReceived?.Invoke(this, lines);
            }
        } catch (IOException) {
            // The server may hold the file for a moment, the next tick tries again.
        } catch (UnauthorizedAccessException) {
            // Same as above.
        } finally {
            Interlocked.Exchange(ref _isReading, 0);
        }
    }
}
