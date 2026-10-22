using System.Diagnostics;
using WidgeDesk.UI.Controls;

namespace WidgeDesk.Dock.Tests;

public sealed class ResumeWallpaperTests
{
    /// <summary>
    /// 静态壁纸源在一次恢复边界上只能产出这一帧。
    /// </summary>
    /// <remarks>
    /// 真实缺陷：喂帧方在读取前先推进了场景版本，而 <c>WallpaperSceneCapture</c> 又让版本跟着
    /// 取样时间走，于是隔离层判定「这一帧早于恢复边界」并丢弃。静态源不会再产出第二帧，
    /// 面板只能停在兜底色上；实测表现为第一次打开正常、第二次打开变成一块灰。
    /// </remarks>
    [Fact]
    public void AResumedStaticWallpaperFrameSurvivesTheSceneBarrier()
    {
        using var capture = new WallpaperSceneCapture(new ScreenRect(0, 0, 8, 4));
        Assert.True(capture.UpdateExclusions());
        var first = capture.TryRead();
        Assert.NotNull(first);

        // 恢复边界：喂帧方先推进场景，再带着更晚的 notBefore 读取。
        capture.UpdateExclusions();
        var resumed = capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp());
        Assert.NotNull(resumed);
        Assert.True(resumed.SourceQpcTicks >= first.SourceQpcTicks);
        Assert.True(resumed.SourceQpcTicks >= capture.SceneRevision);
    }

    [Fact]
    public void ReadingDoesNotChangeTheSceneRevisionItWasReadAgainst()
    {
        using var capture = new WallpaperSceneCapture(new ScreenRect(0, 0, 8, 4));
        capture.UpdateExclusions();
        // 恢复读取会重采样，这一帧必须在读前读后看到同一个场景版本。
        // 喂帧方正是用这个前后比较来判定「读取期间场景是否变了」，
        // 变了就丢弃这一帧——而静态壁纸不会再有下一帧。
        var revision = capture.SceneRevision;
        Assert.NotNull(capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp()));
        Assert.Equal(revision, capture.SceneRevision);
    }
}
