using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Cs2ServerControl.Install;
using Cs2ServerControl.Server;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;
using Xunit;

namespace Cs2ServerControl.Tests;

/// <summary>
/// Builds the main window with every screen, and the dialogs, from their
/// XAML with the real theme, but never shows them: a missing style or
/// resource key only fails when a view is created, which in the app means
/// a window that does not open.
/// </summary>
public class ViewLoadingTests {
    [Fact]
    public void TheMainWindowAndAllScreensLoadWithTheTheme() {
        RunOnStaThread(() => {
            EnsureApplicationWithTheme();
            string rootDir = Path.Combine(Path.GetTempPath(), "cs16-view-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(rootDir, "tools"));
            var settings = new AppSettings { RconPassword = "test-only", FilePath = Path.Combine(rootDir, "settings.json") };
            using var connection = new ServerConnection(new PackagePaths(rootDir), settings, Dispatcher.CurrentDispatcher);
            try {
                var shell = new ShellViewModel(connection);
                var window = new MainWindow(shell, settings);

                Assert.NotNull(window.Content);
                Assert.NotNull(new MapDetailsView());
            } finally {
                FileTree.DeleteFolderQuietly(rootDir);
            }
        });
    }

    [Fact]
    public void DropDownsShowTheLabelNotTheRecord() {
        RunOnStaThread(() => {
            EnsureApplicationWithTheme();
            string rootDir = Path.Combine(Path.GetTempPath(), "cs16-view-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(rootDir, "tools"));
            var settings = new AppSettings { RconPassword = "test-only", FilePath = Path.Combine(rootDir, "settings.json") };
            using var connection = new ServerConnection(new PackagePaths(rootDir), settings, Dispatcher.CurrentDispatcher);
            try {
                var view = new Cs2ServerControl.Options.OptionsView {
                    DataContext = new Cs2ServerControl.Options.OptionsViewModel(connection, [new Cs2ServerControl.Options.OptionChoice("Server", "Server")]),
                };
                // Layout without a window applies the templates and runs the bindings.
                view.Measure(new Size(1600, 1200));
                view.Arrange(new Rect(0, 0, 1600, 1200));
                view.UpdateLayout();

                List<string> texts = TextsIn(view);
                Assert.Contains(texts, text => text.StartsWith("Automatic (", StringComparison.Ordinal));
                Assert.Contains("The screen used last", texts);
                Assert.DoesNotContain(texts, text => text.Contains("OptionChoice", StringComparison.Ordinal));
            } finally {
                FileTree.DeleteFolderQuietly(rootDir);
            }
        });
    }

    [Theory]
    [InlineData("TextInput")]
    [InlineData("RconInput")]
    public void TheCaretStartsWhereThePlaceholderStarts(string styleKey) {
        RunOnStaThread(() => {
            EnsureApplicationWithTheme();
            var box = new System.Windows.Controls.TextBox {
                Style = (Style)Application.Current!.FindResource(styleKey),
                Tag = "Mode default: 25",
                Width = 400,
            };
            box.Measure(new Size(400, 100));
            box.Arrange(new Rect(0, 0, 400, 40));
            box.UpdateLayout();

            var placeholder = (System.Windows.Controls.TextBlock)box.Template.FindName("Placeholder", box);
            double placeholderX = placeholder.TransformToAncestor(box).Transform(new Point(0, 0)).X;
            double caretX = box.GetRectFromCharacterIndex(0).X;

            Assert.InRange(caretX - placeholderX, -1, 1);
        });
    }

    private static List<string> TextsIn(DependencyObject root) {
        var texts = new List<string>();
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++) {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is System.Windows.Controls.TextBlock textBlock) {
                texts.Add(textBlock.Text);
            }
            texts.AddRange(TextsIn(child));
        }
        return texts;
    }

    private static void EnsureApplicationWithTheme() {
        if (Application.Current != null) {
            return;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Theme/Tokens.xaml", "Theme/Controls.xaml" }) {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri($"pack://application:,,,/Cs2ServerControl;component/{dictionary}"),
            });
        }
    }

    private static void RunOnStaThread(Action action) {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => {
            try {
                action();
            } catch (Exception exception) {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
