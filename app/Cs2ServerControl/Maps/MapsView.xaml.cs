using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cs2ServerControl.Maps;

public partial class MapsView : UserControl {
    public MapsView() {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>The newest line of a running map update stays in view.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) {
        if (ActivityLog.ItemsSource is INotifyCollectionChanged lines) {
            lines.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(
                () => FindScrollViewer(ActivityLog)?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // About two rows of cards before the end, so the next page is there before the user gets to it.
    private const double LoadMoreDistance = 600;

    private void OnMarketScrolled(object sender, ScrollChangedEventArgs e) {
        if (DataContext is not MapsViewModel viewModel || !viewModel.IsMarketTab) {
            return;
        }
        bool isNearEnd = MarketScroll.ScrollableHeight - MarketScroll.VerticalOffset < LoadMoreDistance;
        if (isNearEnd && viewModel.Market.CanLoadMore) {
            _ = viewModel.Market.LoadMore();
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
