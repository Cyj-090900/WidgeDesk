using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;

namespace WidgeDesk.Core.Tests;

/// <summary>
/// 「用户从 Dock 上移除过哪些自动项」这份记录的持久化与清理。
/// </summary>
/// <remarks>
/// 自动磁贴本身是运行期状态、不落盘；只有这份「移除过」的记录要存——否则重启之后
/// 用户移除过的应用会立刻回到 Dock 上，看起来就像那个菜单项从来没生效过。
/// </remarks>
public sealed class DockLayoutSuppressionTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void 移除记录跨重启保留()
    {
        var store = _temp.CreateStore().Layouts;
        store.LoadDock();
        store.SuppressAutoItem("auto:c:\\apps\\notepad.exe");
        store.SaveDock();

        var reopened = new LayoutStore(_temp.Paths.DockLayoutFile, _temp.Paths.LauncherLayoutFile);
        reopened.LoadDock();

        Assert.Contains("auto:c:\\apps\\notepad.exe", reopened.Dock.SuppressedAutoItemIds);
    }

    /// <summary>应用已经不在运行时清掉记录，下次再打开它应当重新出现。</summary>
    [Fact]
    public void 应用不再运行后移除记录被清掉()
    {
        var store = _temp.CreateStore().Layouts;
        store.LoadDock();
        store.SuppressAutoItem("auto:a");
        store.SuppressAutoItem("auto:b");

        Assert.True(store.RetainSuppressedAutoItems(["auto:a"]));
        Assert.Equal(["auto:a"], store.Dock.SuppressedAutoItemIds);
    }

    /// <summary>没有变化时不报告「已变化」，调用方据此避免多余的写盘。</summary>
    [Fact]
    public void 没有变化时不报告变化()
    {
        var store = _temp.CreateStore().Layouts;
        store.LoadDock();
        store.SuppressAutoItem("auto:a");

        Assert.False(store.RetainSuppressedAutoItems(["auto:a"]));
        Assert.False(store.ForgetSuppressedAutoItem("auto:不存在"));
    }

    [Fact]
    public void 重复记录同一个自动项不会叠加()
    {
        var store = _temp.CreateStore().Layouts;
        store.LoadDock();
        store.SuppressAutoItem("auto:a");
        store.SuppressAutoItem("auto:a");

        Assert.Single(store.Dock.SuppressedAutoItemIds);
    }

    /// <summary>旧布局文件里没有这个字段，加载后必须是一个可用的空列表。</summary>
    [Fact]
    public void 旧布局文件缺字段时当作空列表()
    {
        File.WriteAllText(_temp.Paths.DockLayoutFile, """{"pinnedItemIds":[]}""");

        var store = new LayoutStore(_temp.Paths.DockLayoutFile, _temp.Paths.LauncherLayoutFile);
        store.LoadDock();

        Assert.NotNull(store.Dock.SuppressedAutoItemIds);
        Assert.Empty(store.Dock.SuppressedAutoItemIds);
    }
}
