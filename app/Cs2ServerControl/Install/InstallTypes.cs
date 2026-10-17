namespace Cs2ServerControl.Install;

public enum InstallMessageKind {
    Step,
    Note,
    Problem,
}

public sealed record InstallMessage(InstallMessageKind Kind, string Text);

/// <summary>A problem that stops the install, with a message meant for the owner.</summary>
public sealed class InstallException : Exception {
    public InstallException(string message) : base(message) {
    }

    public InstallException(string message, Exception innerException) : base(message, innerException) {
    }
}

/// <summary>Turns the three message kinds into short calls for the installer steps.</summary>
internal sealed class InstallLog {
    private readonly IProgress<InstallMessage> _progress;

    public InstallLog(IProgress<InstallMessage> progress) {
        _progress = progress;
    }

    public void Step(string text) {
        _progress.Report(new InstallMessage(InstallMessageKind.Step, text));
    }

    public void Note(string text) {
        _progress.Report(new InstallMessage(InstallMessageKind.Note, text));
    }

    public void Problem(string text) {
        _progress.Report(new InstallMessage(InstallMessageKind.Problem, text));
    }
}
