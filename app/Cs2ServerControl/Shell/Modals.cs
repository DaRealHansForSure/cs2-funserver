using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cs2ServerControl.Shell;

public interface IDialogService {
    Task<bool> ConfirmAsync(string title, string message, string okText);

    /// <summary>Shows a modal view model (for example map details) until it closes itself.</summary>
    Task ShowAsync(ModalViewModel modal);
}

/// <summary>Base of everything shown in the window's modal layer.</summary>
public abstract partial class ModalViewModel : ObservableObject {
    private readonly TaskCompletionSource _closed = new();

    public Task Closed => _closed.Task;

    [RelayCommand]
    public virtual void Close() {
        _closed.TrySetResult();
    }
}

public sealed partial class ConfirmModal : ModalViewModel {
    public ConfirmModal(string title, string message, string okText) {
        Title = title;
        Message = message;
        OkText = okText;
    }

    public string Title { get; }
    public string Message { get; }
    public string OkText { get; }
    public bool Result { get; private set; }

    [RelayCommand]
    private void Ok() {
        Result = true;
        Close();
    }
}

/// <summary>
/// Puts modals into the shell's modal layer, one at a time: a second request
/// waits until the first one is closed.
/// </summary>
public sealed class DialogService : IDialogService {
    private readonly Action<ModalViewModel?> _show;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public DialogService(Action<ModalViewModel?> show) {
        _show = show;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText) {
        var modal = new ConfirmModal(title, message, okText);
        await ShowAsync(modal);
        return modal.Result;
    }

    public async Task ShowAsync(ModalViewModel modal) {
        await _oneAtATime.WaitAsync();
        try {
            _show(modal);
            await modal.Closed;
        } finally {
            _show(null);
            _oneAtATime.Release();
        }
    }
}
