using System.Reflection;
using WidgeDesk.Platform.Displays;
using WidgeDesk.UI.Controls;

namespace WidgeDesk.Dock.Tests;

public sealed class BasicRenderingTests
{
    [Fact]
    public void RendererFailureProvidesReadablePixelsWithoutWaitingForCapture()
    {
        var method = typeof(WidgeDesk.UI.BackdropRecovery).GetMethod("UseBasicRendering");
        Assert.NotNull(method);
        try
        {
            method.Invoke(null, null);
            using var background = new GlassBackgroundLayer();
            var result = background.Capture(new PixelRect(0, 0, 40, 20), 40, 20, 5, true);
            Assert.NotNull(result); Assert.Equal(GlassBackdropSource.Basic, result.Source);
            Assert.True(background.RequiresBasicBackground);
            Assert.Equal(255, result.Pixels[3]); Assert.True(result.Pixels[0] > 0);
        }
        finally { WidgeDesk.UI.BackdropRecovery.RetryBackground(); }
    }
}
