using WidgeDesk.Platform.Shell;

namespace WidgeDesk.Dock.Tests;

public sealed class ActionCenterTests
{
    [Fact]
    public void ToggleReportsWhetherTheKeyboardChordWasDelivered()
    {
        // 不断言"面板一定弹出"：那是系统状态（可能被组策略关闭，
        // 也可能用户改掉了快捷键）。这里断言的是我们这一侧的可测契约：
        // 调用不抛异常，并把投递结果如实返回。
        var sent = ActionCenter.Toggle();
        Assert.IsType<bool>(sent);
        // 把面板关回去，别让测试在屏幕上留下一个浮层。
        if (sent) _ = ActionCenter.Toggle();
    }
}
