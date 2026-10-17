using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cs2ServerControl.Server;

public partial class ServerView : UserControl {
    private ScrollViewer? _consoleScroll;
    private bool _followTail = true;

    public ServerView() {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        if (_consoleScroll != null) {
            return;
        }

        _consoleScroll = FindChild<ScrollViewer>(ConsoleList);
        if (_consoleScroll != null) {
            _consoleScroll.ScrollChanged += OnConsoleScrolled;
        }
        if (ConsoleList.ItemsSource is INotifyCollectionChanged lines) {
            lines.CollectionChanged += (_, _) => ScrollToEndIfFollowing();
        }
        _consoleScroll?.ScrollToEnd();
    }

    /// <summary>
    /// Keeps the newest line in view, but only while the user has not
    /// scrolled up to read something.
    /// </summary>
    private void OnConsoleScrolled(object sender, ScrollChangedEventArgs e) {
        if (_consoleScroll == null) {
            return;
        }
        bool contentGrew = e.ExtentHeightChange > 0;
        if (contentGrew) {
            return;
        }
        bool isAtEnd = _consoleScroll.VerticalOffset >= _consoleScroll.ScrollableHeight - 2;
        // Only the user scrolling up turns Follow off; scrolling back down turns it on again.
        if (isAtEnd != FollowToggle.IsChecked) {
            FollowToggle.IsChecked = isAtEnd;
        }
    }

    private void OnFollowChecked(object sender, RoutedEventArgs e) {
        _followTail = true;
        _consoleScroll?.ScrollToEnd();
    }

    private void OnFollowUnchecked(object sender, RoutedEventArgs e) {
        _followTail = false;
    }

    private void ScrollToEndIfFollowing() {
        if (_followTail) {
            Dispatcher.BeginInvoke(() => _consoleScroll?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) {
                return match;
            }
            T? nested = FindChild<T>(child);
            if (nested != null) {
                return nested;
            }
        }
        return null;
    }
}
