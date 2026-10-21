using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        var results = new List<object>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")
        });
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-notice-test-" + Guid.NewGuid().ToString("n"));
        var report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "report.json");
        Directory.CreateDirectory(root);
        var failed = false;
        LauncherWindow? window = null;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            using var host = new AppServiceHost(data);
            var input = Path.Combine(root, "Test app");
            Directory.CreateDirectory(input);
            var entry = host.Import.ImportItems([new GlassDesk.Core.Contracts.ImportRequest { Source = input }]).Added.Single();
            data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
            data.Layouts.SaveDock();
            window = new LauncherWindow();
            window.AttachServices(host, true);
            window.ShowActivated = false;
            window.Show();
            window.Hide();
            var status = (TextBlock)window.FindName("StatusText")!;
            var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
            var search = (TextBox)window.FindName("SearchBox")!;
            var trash = (FrameworkElement)window.FindName("TrashDropTarget")!;
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new LauncherWorkspace(host).BeginEdit());
            var payload = new DataObject("GlassDesk.Launcher.Slot", entry.Id);
            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [payload, DragDropKeyStates.None, DragDropEffects.Move, trash, new Point(1, 1)], culture: null)!;
            drop.RoutedEvent = DragDrop.DropEvent;
            trash.RaiseEvent(drop);
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
            window.RefreshLibrary();
            Check("Production trash drop removes the launcher tile", grid.Children.Count == 0);
            search.Text = "Test app";
            Check("A removed shortcut is absent from search", grid.Children.Count == 0);
            search.Clear();
            Check("Trash preserves the pinned Dock item", data.Layouts.Dock.PinnedItemIds.Contains(entry.Id));
            Check("Saved layout has no hidden list", !File.ReadAllText(data.Paths.LauncherLayoutFile).Contains("excludedAppIds"));

            Add();
            Check("Adding the same source puts the tile back", grid.Children.Count == 1
                && data.Library.Items.Count == 1 && status.Visibility == Visibility.Visible);

            // 搜索框的一键清除：空查询时按钮藏起来，有内容才出现，点一下清空并收回按钮。
            // 放在入库之后，这样"清空回到浏览视图"的断言才有区分度——此时浏览视图有 1 项。
            // 关掉系统搜索：本机装的应用与索引文件会随防抖异步并入结果，数量不受本测试控制。
            window.ApplySettings(new GlassDesk.Core.Models.Settings.LauncherSettings
            {
                SearchInstalledApps = false,
                SearchFiles = false,
            });
            var clear = (Button)window.FindName("ClearSearchButton")!;
            Check("Clear button is hidden while the search box is empty",
                clear.Visibility == Visibility.Collapsed);
            search.Text = "Test app";
            Pump(300);
            Check("Clear button appears once there is a query",
                clear.Visibility == Visibility.Visible && grid.Children.Count == 1);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Clear button empties the search box and hides itself",
                search.Text.Length == 0 && clear.Visibility == Visibility.Collapsed);
            Check("Clearing returns the grid to the browse view", grid.Children.Count == 1);

            // 打开搜索结果后自动入库：驱动真实代码路径，断言库和布局都变了。
            // 走反射调私有方法而不是调 LaunchSystemResult，是因为后者会真的 ShellExecute
            // 把计算器拉起来——自动化测试不该启动用户的程序。
            var libraryBefore = data.Library.Items.Count;
            var add = typeof(LauncherWindow).GetMethod("AddOpenedResultToLauncher",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            add.Invoke(window, [new GlassDesk.AppServices.SystemSearchResult
            {
                Kind = GlassDesk.AppServices.SystemSearchKind.App,
                DisplayName = "计算器",
                Target = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                AppUserModelId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            }]);
            var opened = data.Library.Items.SingleOrDefault(item => item?.DisplayName == "计算器");
            Check("Opening a search result adds it to the library",
                data.Library.Items.Count == libraryBefore + 1 && opened is not null);
            Check("The opened app carries its shell identity",
                opened?.AppUserModelId == "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
            Check("The opened app lands in the launcher layout",
                opened is not null
                && GlassDesk.Core.Models.LauncherLayoutRules.AppIds(data.Layouts.Launcher).Contains(opened.Id));
            // 文件不该被收进启动台：那会把网格塞满一次性的东西。
            var afterApp = data.Library.Items.Count;
            add.Invoke(window, [new GlassDesk.AppServices.SystemSearchResult
            {
                Kind = GlassDesk.AppServices.SystemSearchKind.File,
                DisplayName = "随手打开的文件.txt",
                Target = Path.Combine(root, "随手打开的文件.txt"),
            }]);
            Check("Opening a file does not grow the library",
                data.Library.Items.Count == afterApp);

            Pump(2400);
            Check("Feedback stays readable before three seconds", status.Visibility == Visibility.Visible);
            Pump(1000);
            Check("Add feedback disappears after three seconds", status.Visibility == Visibility.Collapsed);

            Add();
            Pump(2000);
            Add();
            Pump(1400);
            Check("A repeated success starts a fresh three seconds", status.Visibility == Visibility.Visible);
            Pump(2000);
            Check("Repeated success also expires", status.Visibility == Visibility.Collapsed);

            Add();
            Pump(2000);
            var showNotice = typeof(LauncherWindow).GetMethod("ShowNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var parameters = showNotice.GetParameters().Length == 1
                ? new object[] { "Read or save failed" } : new object[] { "Read or save failed", false };
            showNotice.Invoke(window, parameters);
            Pump(1500);
            Check("An older success cannot erase a newer error", status.Visibility == Visibility.Visible
                && status.Text == "Read or save failed");
            Pump(2000);
            Check("Errors remain available to read", status.Visibility == Visibility.Visible);

            void Add() => typeof(LauncherWindow).GetMethod("ImportPaths",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new List<string> { input }]);
        }
        catch (Exception ex)
        {
            failed = true;
            results.Add(new { name = "Unexpected exception", passed = false, details = ex.ToString() });
        }
        finally
        {
            window?.Close();
            app.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = !failed, results },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        return failed ? 1 : 0;

        void Check(string name, bool passed)
        {
            results.Add(new { name, passed });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
            if (!passed) failed = true;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
