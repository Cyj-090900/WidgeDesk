using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;
using WidgeDesk.UI.Settings;

namespace WidgeDesk.Dock.Tests;

/// <summary>
/// 运行中的应用自动出现在 Dock 上（D-07 的扩展）。
/// </summary>
/// <remarks>
/// 用真实的 <see cref="DockWindow"/> 与真实的 <see cref="AppServiceHost"/>，
/// 只把「窗口快照」换成注入的假数据——归纳、磁贴构建、菜单行为都是生产代码。
/// </remarks>
public sealed class DockAutoTileTests
{
    /// <summary>正在运行、且用户没固定过的应用会出现在 Dock 上。</summary>
    [Fact]
    public void 运行中的应用自动出现在Dock上()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                // 一个不由本进程拥有的假窗口。归纳读的就是这份快照。
                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]); 

                window.RefreshTiles();

                var auto = AutoTiles(window);
                var tile = Assert.Single(auto);
                Assert.Equal("notepad", tile.DisplayName);
                Assert.True(tile.Windows.Count > 0, "自动磁贴没有可用于「退出程序」的窗口句柄");
                Assert.Equal(Visibility.Visible, tile.RunningVisibility);
            });
        });
    }

    /// <summary>已经固定过的应用不再重复出现。</summary>
    [Fact]
    public void 已固定的应用不会重复出现()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                var entry = AppEntryFactory.CreateFromInstalledApp("记事本", @"C:\Apps\notepad.exe", null!);
                data.Library.Add(entry);
                data.Layouts.Dock.PinnedItemIds.Add(entry.Id);

                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]);

                window.RefreshTiles();

                Assert.Empty(AutoTiles(window));
                // 固定项自己还在——不能因为「不让它重复」而把它也去掉。
                Assert.Contains(AllTiles(window), tile => tile.EntryId == entry.Id);
            });
        });
    }

    /// <summary>「从 Dock 移除」之后，那个应用在本次运行内不再自动出现。</summary>
    [Fact]
    public void 移除之后不再自动出现()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]);
                window.RefreshTiles();

                var auto = Assert.Single(AutoTiles(window));

                var remove = typeof(DockWindow).GetMethod("RemoveAutoTile",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                remove.Invoke(window, [auto]);

                // 应用仍在运行，但没有被重新加回来——这就是「移除」该有的效果。
                Assert.Empty(AutoTiles(window));
                Assert.Contains(auto.EntryId, data.Layouts.Dock.SuppressedAutoItemIds);
            });
        });
    }

    /// <summary>「从 Dock 移除」不能碰到用户自己固定的图标。</summary>
    [Fact]
    public void 移除自动项不影响固定项()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                var pinned = AppEntryFactory.CreateFromInstalledApp("浏览器", @"C:\Apps\browser.exe", null!);
                data.Library.Add(pinned);
                data.Layouts.Dock.PinnedItemIds.Add(pinned.Id);

                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]);
                window.RefreshTiles();

                var auto = Assert.Single(AutoTiles(window));
                var remove = typeof(DockWindow).GetMethod("RemoveAutoTile",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                remove.Invoke(window, [auto]);

                // 固定项必须原样保留：这是「两个列表分开」最直接验证。
                Assert.Contains(pinned.Id, data.Layouts.Dock.PinnedItemIds);
            });
        });
    }

    /// <summary>
    /// 应用彻底关掉之后，自动磁贴自己消失，而且移除记录被清掉。
    /// </summary>
    /// <remarks>
    /// 清掉移除记录是必须的：否则用户这次移除过它，下次再打开就再也不出现了——
    /// 那是把一次性的动作记成了永久决定。
    /// </remarks>
    [Fact]
    public void 应用关掉后磁贴消失且移除记录被清掉()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]);
                window.RefreshTiles();
                var auto = Assert.Single(AutoTiles(window));

                var remove = typeof(DockWindow).GetMethod("RemoveAutoTile",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                remove.Invoke(window, [auto]);
                Assert.Contains(auto.EntryId, data.Layouts.Dock.SuppressedAutoItemIds);

                // 应用退出了。
                host.Running.ApplySnapshotForTesting([]);
                var sync = typeof(DockWindow).GetMethod("SyncAutoTiles",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                sync.Invoke(window, null);

                Assert.Empty(AutoTiles(window));
                Assert.DoesNotContain(auto.EntryId, data.Layouts.Dock.SuppressedAutoItemIds);

                // 再运行一次时就该回来了。
                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "记事本", 1234, false, @"C:\Apps\notepad.exe", null, null)]);
                window.RefreshTiles();
                Assert.Single(AutoTiles(window));
            });
        });
    }

    /// <summary>本进程自己的窗口不会变成磁贴——否则 Dock 会把自己画上去。</summary>
    [Fact]
    public void 本进程窗口不会变成磁贴()
    {
        StaTest.Run(() =>
        {
            WithDock((window, host, data) =>
            {
                host.Running.ApplySnapshotForTesting([
                    new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                        4242, "Dock", (uint)Environment.ProcessId, false, @"C:\Apps\widgedesk.exe", null, null)]);

                window.RefreshTiles();

                Assert.Empty(AutoTiles(window));
            });
        });
    }

    /// <summary>
    /// 对<strong>真实运行的进程</strong>点一次自动磁贴，把它的窗口提到前面，
    /// 而不是再启动一个。
    /// </summary>
    /// <remarks>
    /// <b>这一条对应一个真实上报的缺陷。</b>应用已经开着，在 Dock 上再点一次却多开一个。
    /// 前面的假句柄测试证明不了这件事——假句柄一律「已失效」，走的永远是兜底分支。
    /// 只有真的起一个带窗口的进程，才能验证「有窗口就前置、绝不启动」。
    /// </remarks>
    [Fact]
    public void 点已运行的自动磁贴只前置不重复启动()
    {
        StaTest.Run(() =>
        {
            // 记事本是最合适的靶子：每次启动都开一个独立的新窗口，
            // 因此「多开」这件事一旦发生就是可数的。
            var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");

            if (!File.Exists(notepad))
            {
                return; // 没有靶子的精简系统上跳过，而不是让整轮失败。
            }

            var before = WidgeDesk.Platform.Shell.WindowLocator.FindWindowsForImage(notepad).Count;
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo(notepad) { UseShellExecute = true });
                Assert.NotNull(process);

                // 等窗口出现；记事本冷启动有时要一秒多。
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (WidgeDesk.Platform.Shell.WindowLocator.FindWindowsForImage(notepad).Count <= before)
                {
                    Assert.True(DateTime.UtcNow < deadline, "记事本窗口没有出现");
                    Thread.Sleep(100);
                }

                var countAfterLaunch = WidgeDesk.Platform.Shell.WindowLocator.FindWindowsForImage(notepad).Count;

                WithDock((window, host, data) =>
                {
                    host.Running.Reconcile();
                    window.RefreshTiles();

                    var auto = AutoTiles(window).FirstOrDefault(tile =>
                        string.Equals(tile.LaunchTarget, notepad, StringComparison.OrdinalIgnoreCase));

                    Assert.NotNull(auto);
                    Assert.NotEmpty(auto.Windows);

                    // 真正点一次。它会前置已有窗口；绝不能启动第二个。
                    var activate = typeof(DockWindow).GetMethod("ActivateAutoTile",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    activate.Invoke(window, [auto.EntryId]);

                    Thread.Sleep(700);

                    var countAfterClick = WidgeDesk.Platform.Shell.WindowLocator.FindWindowsForImage(notepad).Count;
                    Assert.True(countAfterClick <= countAfterLaunch,
                        $"点一次之后多出了窗口：{countAfterLaunch} -> {countAfterClick}");
                });
            }
            finally
            {
                try
                {
                    if (process is { HasExited: false }) process.Kill();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process?.Dispose();
            }
        });
    }

    private static void WithDock(Action<DockWindow, AppServiceHost, WidgeDeskDataStore> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-dock-auto-" + Guid.NewGuid().ToString("N"));
        DockWindow? window = null;
        try
        {
            var data = new WidgeDeskDataStore(new AppPaths(root));
            data.LoadAll();
            using var host = new AppServiceHost(data);
            var settings = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            // 这些用例断言的是「窗口快照 → 自动磁贴」这条路径。
            // 关掉通知区扫描，否则结果会取决于跑测试的这台机器恰好开着什么后台程序。
            window = new DockWindow { ReadTrayApps = () => [] };
            window.AttachServices(host, settings, () => { });
            body(window, host, data);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static List<DockTileModel> AllTiles(DockWindow window)
        => window.TileModels.ToList();

    private static List<DockTileModel> AutoTiles(DockWindow window)
        => window.TileModels.Where(model => model.IsAuto).ToList();
}
