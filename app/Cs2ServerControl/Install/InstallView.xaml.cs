using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cs2ServerControl.Install;

public partial class InstallView : UserControl {
    public InstallView() {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>The newest log line stays in view while the install runs.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) {
        if (LogList.ItemsSource is INotifyCollectionChanged lines) {
            lines.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(
                () => FindScrollViewer(LogList)?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent) {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) {
                return viewer;
            }
            ScrollViewer? nested = FindScrollViewer(child);
            if (nested != null) {
                return nested;
            }
        }
        return null;
    }
}
