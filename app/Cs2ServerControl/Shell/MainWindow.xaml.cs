using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Shell;

public partial class MainWindow : Window {
    // Win32 messages and hit test codes for the Windows 11 snap layout
    // flyout, which only appears when the system sees a real maximize button.
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HitMaximizeButton = 9;

    private const string MaximizeGlyphData = "M 0.5 0.5 L 9.5 0.5 L 9.5 9.5 L 0.5 9.5 Z";
    private const string RestoreGlyphData = "M 2.5 2.5 L 2.5 0.5 L 9.5 0.5 L 9.5 7.5 L 7.5 7.5 M 0.5 2.5 L 7.5 2.5 L 7.5 9.5 L 0.5 9.5 Z";

    private readonly AppSettings _settings;

    public MainWindow(ShellViewModel viewModel, AppSettings settings) {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        RestoreWindowPlacement();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
        StateChanged += (_, _) => UpdateMaximizedLayout();
        UpdateMaximizedLayout();
        viewModel.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ShellViewModel.Modal)) {
                UpdateModalLayer(viewModel.Modal != null);
            }
            if (e.PropertyName == nameof(ShellViewModel.CurrentScreen)) {
                Dispatcher.BeginInvoke(FocusShownScreen, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    private IInputElement? _focusBeforeModal;

    /// <summary>
    /// A hidden screen must not keep the keyboard: W/A/S/D meant for the 3D
    /// camera would otherwise land in the hidden RCON text box. The rail item
    /// that was clicked keeps the focus, the keys go to the shown screen.
    /// </summary>
    private void FocusShownScreen() {
        if (LiveMapScreen.IsVisible) {
            LiveMapScreen.FocusViewport();
            return;
        }
        if (Keyboard.FocusedElement is DependencyObject focused && !IsInsideVisibleScreen(focused)) {
            Keyboard.ClearFocus();
        }
    }

    private bool IsInsideVisibleScreen(DependencyObject element) {
        FrameworkElement[] screens = [LiveMapScreen, ServerScreen, BotsScreen, MapsScreen, StatsScreen, InstallScreen, OptionsScreen];
        foreach (FrameworkElement screen in screens) {
            if (screen.IsVisible && screen.IsAncestorOf(element)) {
                return true;
            }
        }
        // The rail, the title bar and the modal layer are always fine.
        return !screens.Any(screen => screen.IsAncestorOf(element));
    }

    /// <summary>
    /// Shows the scrim and moves the keyboard into the dialog; on close the
    /// focus goes back to where it was, so keyboard users do not get lost.
    /// </summary>
    private void UpdateModalLayer(bool isOpen) {
        if (isOpen) {
            _focusBeforeModal = Keyboard.FocusedElement;
            ModalLayer.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(() => ModalContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        ModalLayer.Visibility = Visibility.Collapsed;
        if (_focusBeforeModal is UIElement element && element.IsVisible) {
            element.Focus();
        }
        _focusBeforeModal = null;
    }

    protected override void OnClosing(CancelEventArgs e) {
        SaveWindowPlacement();
        base.OnClosing(e);
    }

    private void RestoreWindowPlacement() {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        bool hasPosition = _settings.WindowLeft != null && _settings.WindowTop != null;
        bool isOnScreen = hasPosition
            && _settings.WindowLeft!.Value < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && _settings.WindowTop!.Value < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100
            && _settings.WindowLeft.Value > SystemParameters.VirtualScreenLeft - Width + 100
            && _settings.WindowTop.Value >= SystemParameters.VirtualScreenTop;
        if (isOnScreen) {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft!.Value;
            Top = _settings.WindowTop!.Value;
        } else {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_settings.WindowMaximized) {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement() {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    private void UpdateMaximizedLayout() {
        bool isMaximized = WindowState == WindowState.Maximized;
        // A maximized chrome window hangs over the screen edge by the resize
        // border, so the content needs that much margin to stay visible.
        Thickness overhang = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = isMaximized
            ? new Thickness(overhang.Left + 2, overhang.Top + 2, overhang.Right + 2, overhang.Bottom + 2)
            : new Thickness(0);
        MaximizeGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        MaximizeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, isMaximized ? "Restore" : "Maximize");
    }

    private IntPtr WindowProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
        switch (message) {
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam)) {
                    MaximizeButton.Tag = "Hover";
                    handled = true;
                    return new IntPtr(HitMaximizeButton);
                }
                MaximizeButton.Tag = null;
                break;
            case WmNcLeftButtonDown:
                if (wParam.ToInt32() == HitMaximizeButton) {
                    // Swallowed here, the click happens on button up like a normal button.
                    handled = true;
                }
                break;
            case WmNcLeftButtonUp:
                if (wParam.ToInt32() == HitMaximizeButton) {
                    ToggleMaximize();
                    handled = true;
                }
                break;
            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam) {
        int packed = unchecked((int)lParam.ToInt64());
        var screenPoint = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        try {
            Point local = MaximizeButton.PointFromScreen(screenPoint);
            return local.X >= 0 && local.Y >= 0 && local.X < MaximizeButton.ActualWidth && local.Y < MaximizeButton.ActualHeight;
        } catch (InvalidOperationException) {
            return false;
        }
    }

    private void ToggleMaximize() {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e) {
        ToggleMaximize();
    }

    private void OnClose(object sender, RoutedEventArgs e) {
        Close();
    }
}
